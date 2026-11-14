using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;

namespace MapChose;

public sealed class MapChosePlugin : BasePlugin
{
    public override string ModuleName => "MapChose";
    public override string ModuleVersion => "1.0.0";
    public override string ModuleAuthor => "JekProGamer";
    public override string ModuleDescription => "Alternates between two configured CS2 Workshop maps.";

    private string[] _maps = Array.Empty<string>();
    private string _currentMap = string.Empty;
    private bool _switching;

    public override void Load(bool hotReload)
    {
        LoadMapList();

        RegisterListener<Listeners.OnMapStart>(map =>
        {
            _currentMap = map;
            _switching = false;
            Logger.LogInformation("MapChose: current map is {Map}", map);
        });

        RegisterListener<Listeners.OnMapEnd>(() =>
        {
            if (_switching || _maps.Length != 2)
                return;

            var currentIndex = Array.FindIndex(
                _maps,
                map => string.Equals(map, _currentMap, StringComparison.OrdinalIgnoreCase));

            var nextMap = currentIndex < 0
                ? _maps[0]
                : _maps[(currentIndex + 1) % _maps.Length];

            _switching = true;
            Logger.LogInformation("MapChose: changing map from {Current} to {Next}", _currentMap, nextMap);

            Server.ExecuteCommand($"ds_workshop_changelevel {nextMap}");
        });

        Logger.LogInformation("MapChose loaded. Configured maps: {Count}", _maps.Length);
    }

    private void LoadMapList()
    {
        var path = Path.Combine(ModuleDirectory, "maplist.txt");

        if (!File.Exists(path))
        {
            File.WriteAllLines(path, new[]
            {
                "dm_nixware_mirage_d",
                "dm_nixware_fysnow_d"
            });
        }

        _maps = File.ReadAllLines(path)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith("#"))
            .Where(line => Regex.IsMatch(line, @"^[a-zA-Z0-9_]+$"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (_maps.Length != 2)
        {
            Logger.LogError("MapChose: maplist.txt must contain exactly 2 valid map names; found {Count}.", _maps.Length);
        }
    }
}
