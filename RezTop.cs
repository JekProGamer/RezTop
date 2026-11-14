using System.Text.Json;
using System.Text.RegularExpressions;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;

namespace RezTop;

public sealed class RezTopPlugin : BasePlugin
{
    public override string ModuleName => "RezTop";
    public override string ModuleVersion => "1.0.0";
    public override string ModuleAuthor => "RezTop";
    public override string ModuleDescription => "Team survival mode with lives and map rotation";

    private const int StartingLives = 10;
    private const int ScoreToWin = 10;
    private readonly Dictionary<ulong, int> _lives = new();
    private readonly HashSet<ulong> _deathInProgress = new();
    private readonly List<string> _maps = new();
    private MessageConfig _messages = new();
    private int _ctScore, _tScore;
    private bool _roundActive, _roundEnding, _changingMap;

    public override void Load(bool hotReload)
    {
        LoadFiles();
        Server.ExecuteCommand("mp_autoteambalance 0");
        Server.ExecuteCommand("mp_limitteams 0");
        Server.ExecuteCommand("mp_respawn_on_death_t 0");
        Server.ExecuteCommand("mp_respawn_on_death_ct 0");
        Server.ExecuteCommand("mp_ignore_round_win_conditions 1");
        Server.ExecuteCommand("mp_freezetime 0");
        Server.ExecuteCommand("mp_roundtime 60");
        Server.ExecuteCommand("mp_roundtime_defuse 60");
        Server.ExecuteCommand("mp_roundtime_hostage 60");
        RegisterEventHandler<EventRoundStart>(OnRoundStart);
        RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath, HookMode.Post);
        RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
        Logger.LogInformation("RezTop loaded. Maps: {Count}", _maps.Count);
    }

    private void LoadFiles()
    {
        try
        {
            var messagesPath = Path.Combine(ModuleDirectory, "messages.json");
            if (File.Exists(messagesPath))
                _messages = JsonSerializer.Deserialize<MessageConfig>(File.ReadAllText(messagesPath),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new MessageConfig();

            var mapPath = Path.Combine(ModuleDirectory, "maplist.txt");
            if (File.Exists(mapPath))
                _maps.AddRange(File.ReadAllLines(mapPath).Select(x => x.Trim())
                    .Where(x => x.Length > 0 && !x.StartsWith("#"))
                    .Where(x => Regex.IsMatch(x, @"^(workshop:)?[a-zA-Z0-9_]+$", RegexOptions.IgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception ex) { Logger.LogError(ex, "Failed to load RezTop configuration"); }
    }

    private HookResult OnRoundStart(EventRoundStart ev, GameEventInfo info)
    {
        if (_changingMap) return HookResult.Continue;
        _roundActive = true;
        _roundEnding = false;
        _deathInProgress.Clear();
        _lives.Clear();
        foreach (var p in Utilities.GetPlayers())
        {
            if (!IsPlayingTeam(p)) continue;
            _lives[p.SteamID] = StartingLives;
            Send(p, "Lives_Reset", ("LIVES", StartingLives.ToString()));
        }
        return HookResult.Continue;
    }

    private HookResult OnPlayerDeath(EventPlayerDeath ev, GameEventInfo info)
    {
        var p = ev.Userid;
        if (p == null || !p.IsValid || !_roundActive || _roundEnding || _changingMap || !IsPlayingTeam(p))
            return HookResult.Continue;

        var id = p.SteamID;
        if (!_deathInProgress.Add(id)) return HookResult.Continue;
        var lives = Math.Max(0, (_lives.TryGetValue(id, out var current) ? current : StartingLives) - 1);
        _lives[id] = lives;
        Send(p, "Life_Lost", ("LIVES", lives.ToString()));

        if (lives > 0)
        {
            AddTimer(0.2f, () =>
            {
                _deathInProgress.Remove(id);
                if (!p.IsValid || !_roundActive || _roundEnding || _changingMap ||
                    !_lives.TryGetValue(id, out var remaining) || remaining <= 0) return;
                try { p.Respawn(); }
                catch (Exception ex) { Logger.LogError(ex, "Could not respawn player {Name}", p.PlayerName); }
                AddTimer(0.25f, CheckForWinner);
            });
        }
        else
        {
            _deathInProgress.Remove(id);
            Send(p, "No_Lives");
            AddTimer(0.1f, CheckForWinner);
        }
        return HookResult.Continue;
    }

    private HookResult OnPlayerDisconnect(EventPlayerDisconnect ev, GameEventInfo info)
    {
        var p = ev.Userid;
        if (p != null)
        {
            _lives.Remove(p.SteamID);
            _deathInProgress.Remove(p.SteamID);
            AddTimer(0.1f, CheckForWinner);
        }
        return HookResult.Continue;
    }

    private void CheckForWinner()
    {
        if (!_roundActive || _roundEnding || _changingMap) return;
        var players = Utilities.GetPlayers().Where(p => p.IsValid).ToList();
        var ct = players.Where(p => p.TeamNum == (byte)CsTeam.CounterTerrorist).ToList();
        var t = players.Where(p => p.TeamNum == (byte)CsTeam.Terrorist).ToList();
        if (ct.Count == 0 || t.Count == 0) return;
        bool ctAlive = ct.Any(IsAlive), tAlive = t.Any(IsAlive);
        if (ctAlive && tAlive) return;
        if (!ctAlive && !tAlive)
        {
            _roundEnding = true;
            _roundActive = false;
            AddTimer(0.1f, () => GetGameRules()?.TerminateRound(0.1f, RoundEndReason.RoundDraw));
            return;
        }
        AwardRound(ctAlive ? CsTeam.CounterTerrorist : CsTeam.Terrorist);
    }

    private void AwardRound(CsTeam winner)
    {
        if (_roundEnding || _changingMap) return;
        _roundEnding = true;
        _roundActive = false;
        if (winner == CsTeam.CounterTerrorist) _ctScore++; else _tScore++;
        var replacements = new[] { ("CT_SCORE", _ctScore.ToString()), ("T_SCORE", _tScore.ToString()) };
        if (winner == CsTeam.CounterTerrorist) { Broadcast("CT_Winner", replacements); Broadcast("T_Loser"); }
        else { Broadcast("CT_Loser"); Broadcast("T_Winner", replacements); }

        if (_ctScore >= ScoreToWin || _tScore >= ScoreToWin)
        {
            _changingMap = true;
            var next = GetNextMap();
            if (next == null) { Broadcast("MapList_Empty"); Logger.LogWarning("maplist.txt is empty or missing"); return; }
            Broadcast("Map_Change", ("MAP", next));
            AddTimer(3.0f, () =>\n            {\n                if (next.StartsWith("workshop:", StringComparison.OrdinalIgnoreCase))\n                    Server.ExecuteCommand($"ds_workshop_changelevel {next[9..]}");\n                else\n                    Server.ExecuteCommand($"changelevel {next}");\n            });
            return;
        }
        var reason = winner == CsTeam.CounterTerrorist ? RoundEndReason.CTsWin : RoundEndReason.TerroristsWin;
        AddTimer(0.1f, () => GetGameRules()?.TerminateRound(0.1f, reason));
    }

    private string? GetNextMap()
    {
        if (_maps.Count == 0) return null;
        var index = _maps.FindIndex(m => string.Equals(m, Server.MapName, StringComparison.OrdinalIgnoreCase));
        return _maps[index < 0 ? 0 : (index + 1) % _maps.Count];
    }

    private static CCSGameRules? GetGameRules()
    {
        try { return Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault()?.GameRules; }
        catch { return null; }
    }

    private static bool IsPlayingTeam(CCSPlayerController p) => p.IsValid &&
        (p.TeamNum == (byte)CsTeam.CounterTerrorist || p.TeamNum == (byte)CsTeam.Terrorist);

    private static bool IsAlive(CCSPlayerController p)
    {
        if (!p.IsValid) return false;
        var pawn = p.PlayerPawn.Value;
        return pawn != null && pawn.IsValid && pawn.Health > 0;
    }

    private void Broadcast(string key, params (string Key, string Value)[] replacements)
    {
        foreach (var p in Utilities.GetPlayers()) Send(p, key, replacements);
    }

    private void Send(CCSPlayerController p, string key, params (string Key, string Value)[] replacements)
    {
        if (!p.IsValid) return;
        var body = _messages.Messages.TryGetValue(key, out var value) ? value : $"{{RED}}[Missing message: {key}]";
        foreach (var item in replacements)
            body = body.Replace("{" + item.Key + "}", item.Value, StringComparison.OrdinalIgnoreCase);
        p.PrintToChat(FormatColors(_messages.Prefix + " " + body));
    }

    private static string FormatColors(string s) => s
        .Replace("{DEFAULT}", ChatColors.Default.ToString())
        .Replace("{WHITE}", ChatColors.White.ToString())
        .Replace("{RED}", ChatColors.Red.ToString())
        .Replace("{GREEN}", ChatColors.Green.ToString())
        .Replace("{BLUE}", ChatColors.Blue.ToString())
        .Replace("{ORANGE}", ChatColors.Orange.ToString())
        .Replace("{YELLOW}", ChatColors.Yellow.ToString())
        .Replace("{PURPLE}", ChatColors.Purple.ToString());

    private sealed class MessageConfig
    {
        public string Prefix { get; set; } = "{WHITE} [{ORANGE}Rez{RED}Top{WHITE}. cc]";
        public Dictionary<string, string> Messages { get; set; } = new()
        {
            ["CT_Winner"] = "{GREEN}CT WINNER! {WHITE}Счёт: {GREEN}{CT_SCORE}{WHITE}:{RED}{T_SCORE}",
            ["T_Winner"] = "{RED}T WINNER! {WHITE}Счёт: {GREEN}{CT_SCORE}{WHITE}:{RED}{T_SCORE}",
            ["CT_Loser"] = "{RED}CT LOSER!", ["T_Loser"] = "{RED}T LOSER!",
            ["Life_Lost"] = "{ORANGE}Ты потерял жизнь! Осталось: {WHITE}{LIVES}",
            ["No_Lives"] = "{RED}У тебя закончились жизни! Ты ждёшь следующий раунд.",
            ["Lives_Reset"] = "{GREEN}Новый раунд! Твои жизни восстановлены: {WHITE}{LIVES}",
            ["Map_Change"] = "{ORANGE}Матч завершён! Загружается следующая карта: {GREEN}{MAP}",
            ["MapList_Empty"] = "{RED}Список карт пуст! Проверь maplist.txt."
        };
    }
}
