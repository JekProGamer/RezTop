using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Utils;
using MySqlConnector;

namespace RezTopPhotoSystem;

public sealed class PhotoSystemPlugin : BasePlugin
{
    public override string ModuleName => "RezTop PhotoSystem";
    public override string ModuleVersion => "0.1.0";
    public override string ModuleAuthor => "RezTop";
    public override string ModuleDescription => "Large map-specific wall photo panels stored in MySQL";

    private PhotoConfig _config = new();
    private string _connectionString = "";
    private string _loadedMap = "";
    private readonly Dictionary<long, CBaseModelEntity> _spawned = new();

    public override void Load(bool hotReload)
    {
        LoadConfig();
        AddCommand("css_photo_add", "Place a large photo panel where you aim (admin only)", CmdAdd);
        AddCommand("css_photo_list", "List photo panels on this map (admin only)", CmdList);
        AddCommand("css_photo_remove", "Remove a photo panel by ID (admin only)", CmdRemove);
        RegisterEventHandler<EventRoundStart>(OnRoundStart);
        _ = EnsureDatabaseAsync();
        Logger.LogInformation("RezTop PhotoSystem loaded. Admin permission: {Permission}", _config.Permission);
    }

    private void LoadConfig()
    {
        var path = Path.Combine(ModuleDirectory, "config.json");
        if (!File.Exists(path))
        {
            File.WriteAllText(path, JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            _config = JsonSerializer.Deserialize<PhotoConfig>(File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new PhotoConfig();
        }

        _connectionString = _config.MySql.ConnectionString;
    }

    private async Task EnsureDatabaseAsync()
    {
        try
        {
            await using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS reztop_photos (
                    id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                    map_name VARCHAR(128) NOT NULL,
                    photo_name VARCHAR(64) NOT NULL,
                    pos_x FLOAT NOT NULL,
                    pos_y FLOAT NOT NULL,
                    pos_z FLOAT NOT NULL,
                    pitch FLOAT NOT NULL,
                    yaw FLOAT NOT NULL,
                    roll FLOAT NOT NULL,
                    created_by BIGINT UNSIGNED NOT NULL,
                    created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    INDEX idx_map_name (map_name)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
                """;
            await command.ExecuteNonQueryAsync();
            Logger.LogInformation("RezTop PhotoSystem: MySQL table is ready.");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "RezTop PhotoSystem: MySQL initialization failed.");
        }
    }

    private HookResult OnRoundStart(EventRoundStart ev, GameEventInfo info)
    {
        var map = Server.MapName;
        if (string.Equals(_loadedMap, map, StringComparison.OrdinalIgnoreCase))
            return HookResult.Continue;

        _loadedMap = map;
        _ = LoadMapPhotosAsync(map);
        return HookResult.Continue;
    }

    private async Task LoadMapPhotosAsync(string map)
    {
        try
        {
            await using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, photo_name, pos_x, pos_y, pos_z, pitch, yaw, roll
                FROM reztop_photos WHERE map_name = @map ORDER BY id;
                """;
            command.Parameters.AddWithValue("@map", map);
            var rows = new List<PhotoRow>();
            await using (var reader = await command.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    rows.Add(new PhotoRow(reader.GetInt64(0), reader.GetString(1),
                        reader.GetFloat(2), reader.GetFloat(3), reader.GetFloat(4),
                        reader.GetFloat(5), reader.GetFloat(6), reader.GetFloat(7)));
                }
            }

            Server.NextFrame(() =>
            {
                foreach (var row in rows)
                    SpawnPanel(row);
                Logger.LogInformation("Loaded {Count} photo panels for {Map}", rows.Count, map);
            });
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load photo panels for map {Map}", map);
        }
    }

    private void CmdAdd(CCSPlayerController? player, CommandInfo info)
    {
        if (!IsAdmin(player)) return;
        if (player == null || !player.PlayerPawn.IsValid)
        {
            Reply(player, "Команда доступна только игроку-администратору.");
            return;
        }
        if (info.ArgCount < 2)
        {
            Reply(player, "Использование: !photo_add <name>");
            return;
        }

        var name = info.GetArg(1).Trim().ToLowerInvariant();
        if (name.Length is < 1 or > 64 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_' || c == '-'))
        {
            Reply(player, "Имя: только латинские буквы, цифры, _ и - (до 64 символов).");
            return;
        }

        var imagePath = Path.Combine(ModuleDirectory, _config.ImageDirectory, name + ".jpg");
        var pngPath = Path.Combine(ModuleDirectory, _config.ImageDirectory, name + ".png");
        var modelPath = $"{_config.PhotoModelDirectory.TrimEnd('/')}/{name}.vmdl";
        if (!File.Exists(imagePath) && !File.Exists(pngPath))
        {
            Reply(player, $"Не найден images/{name}.jpg или .png (FTP).");
            return;
        }

        var pawn = player.PlayerPawn.Value!;
        var origin = pawn.AbsOrigin;
        var eye = new Vector(origin.X, origin.Y, origin.Z + 64.0f);
        var trace = Trace.TraceShape(eye, pawn.EyeAngles, pawn);
        if (!trace.DidHit())
        {
            Reply(player, "Не вижу стену/поверхность. Наведи прицел на неё.");
            return;
        }

        var hit = trace.HitPoint;
        var normal = trace.Normal;
        var yaw = MathF.Atan2(normal.Y, normal.X) * (180.0f / MathF.PI);
        var row = new PhotoRow(0, name, hit.X + normal.X * 2.0f, hit.Y + normal.Y * 2.0f,
            hit.Z + normal.Z * 2.0f, 0.0f, yaw, 0.0f);

        Server.NextFrame(async () =>
        {
            if (!SpawnPanel(row with { Id = 0 }, modelPath, out var entity))
            {
                Reply(player, $"Не удалось создать панель. Проверь модель {modelPath} и загрузку ресурсов клиентам.");
                return;
            }

            try
            {
                var id = await InsertPhotoAsync(Server.MapName, row, player.SteamID);
                _spawned[id] = entity!;
                Reply(player, $"Фотография {name} поставлена. ID: {id}");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Could not save photo placement.");
                entity!.Remove();
                Reply(player, "Ошибка сохранения в MySQL. Панель удалена.");
            }
        });
    }

    private async Task<long> InsertPhotoAsync(string map, PhotoRow row, ulong steamId)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO reztop_photos
            (map_name, photo_name, pos_x, pos_y, pos_z, pitch, yaw, roll, created_by)
            VALUES (@map, @name, @x, @y, @z, @pitch, @yaw, @roll, @admin);
            SELECT LAST_INSERT_ID();
            """;
        command.Parameters.AddWithValue("@map", map);
        command.Parameters.AddWithValue("@name", row.Name);
        command.Parameters.AddWithValue("@x", row.X);
        command.Parameters.AddWithValue("@y", row.Y);
        command.Parameters.AddWithValue("@z", row.Z);
        command.Parameters.AddWithValue("@pitch", row.Pitch);
        command.Parameters.AddWithValue("@yaw", row.Yaw);
        command.Parameters.AddWithValue("@roll", row.Roll);
        command.Parameters.AddWithValue("@admin", steamId);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private void CmdList(CCSPlayerController? player, CommandInfo info)
    {
        if (!IsAdmin(player)) return;
        if (player == null) return;
        _ = ListPhotosAsync(player, Server.MapName);
    }

    private async Task ListPhotosAsync(CCSPlayerController player, string map)
    {
        try
        {
            await using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, photo_name FROM reztop_photos WHERE map_name=@map ORDER BY id";
            command.Parameters.AddWithValue("@map", map);
            var lines = new List<string>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                lines.Add($"#{reader.GetInt64(0)} — {reader.GetString(1)}");
            Server.NextFrame(() => Reply(player, lines.Count == 0 ? "На этой карте фотографий нет." : string.Join(" | ", lines)));
        }
        catch (Exception ex) { Logger.LogError(ex, "Photo list query failed."); }
    }

    private void CmdRemove(CCSPlayerController? player, CommandInfo info)
    {
        if (!IsAdmin(player)) return;
        if (player == null || info.ArgCount < 2 || !long.TryParse(info.GetArg(1), out var id))
        {
            Reply(player, "Использование: !photo_remove <id>");
            return;
        }
        _ = RemovePhotoAsync(player, id, Server.MapName);
    }

    private async Task RemovePhotoAsync(CCSPlayerController player, long id, string map)
    {
        try
        {
            await using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM reztop_photos WHERE id=@id AND map_name=@map";
            command.Parameters.AddWithValue("@id", id);
            command.Parameters.AddWithValue("@map", map);
            var changed = await command.ExecuteNonQueryAsync();
            Server.NextFrame(() =>
            {
                if (_spawned.Remove(id, out var entity) && entity.IsValid)
                    entity.Remove();
                Reply(player, changed > 0 ? $"Фотография #{id} удалена." : "Фотография не найдена на этой карте.");
            });
        }
        catch (Exception ex) { Logger.LogError(ex, "Photo removal failed."); }
    }

    private bool SpawnPanel(PhotoRow row)
    {
        var modelPath = $"{_config.PhotoModelDirectory.TrimEnd('/')}/{row.Name}.vmdl";
        return SpawnPanel(row, modelPath, out _);
    }

    private bool SpawnPanel(PhotoRow row, string modelPath, out CBaseModelEntity? entity)
    {
        entity = null;
        try
        {
            var panel = Utilities.CreateEntityByName<CBaseModelEntity>("prop_dynamic");
            if (panel == null || !panel.IsValid) return false;
            panel.SetModel(modelPath);
            panel.Teleport(new Vector(row.X, row.Y, row.Z), new QAngle(row.Pitch, row.Yaw, row.Roll), new Vector(0, 0, 0));
            panel.DispatchSpawn();
            entity = panel;
            if (row.Id > 0) _spawned[row.Id] = panel;
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Could not spawn model {Model}", modelPath);
            return false;
        }
    }

    private bool IsAdmin(CCSPlayerController? player)
    {
        if (player == null || !player.IsValid) return false;
        if (AdminManager.PlayerHasPermissions(player, _config.Permission)) return true;
        Reply(player, "У тебя нет прав администратора для PhotoSystem.");
        return false;
    }

    private static void Reply(CCSPlayerController? player, string message)
    {
        if (player != null && player.IsValid)
            player.PrintToChat($"\x04[RezTop PhotoSystem]\x01 {message}");
    }

    private sealed class PhotoConfig
    {
        public MySqlConfig MySql { get; set; } = new();
        public string Permission { get; set; } = "@css/generic";
        public string PhotoModelDirectory { get; set; } = "models/reztop/photos";
        public string ImageDirectory { get; set; } = "images";
        public float MaxTraceDistance { get; set; } = 250.0f;
        public float DefaultScale { get; set; } = 1.0f;
    }

    private sealed class MySqlConfig
    {
        public string ConnectionString { get; set; } = "Server=127.0.0.1;Database=reztop;User ID=USERNAME;Password=PASSWORD;";
    }

    private sealed record PhotoRow(long Id, string Name, float X, float Y, float Z, float Pitch, float Yaw, float Roll);
}
