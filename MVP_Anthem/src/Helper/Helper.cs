using AudioApi;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.SchemaDefinitions;
using SwiftlyS2.Shared.Sounds;

namespace MVP_Anthem;

public static class Helper
{
    public static ISwiftlyCore Core => Main.Core;
    private static readonly Dictionary<int, HtmlSendEntry> SendMessages = [];
    private static readonly List<int> ExpiredMessages = [];
    private static int _generation;

    public static void Reset()
    {
        _generation++;
        ClearHtmlMessages();
    }
    public static string GetPlayerName(IPlayer player)
    {
        return player.Controller.PlayerName ?? "Console";
    }

    public static float ClampVolume(float volume)
    {
        return float.IsNaN(volume) ? 0f : Math.Clamp(volume, 0f, 1f);
    }

    public static (string MvpName, string SoundPath)? GetRandomMvp(MVPConfig config)
    {
        var allMvps = config.MVPs.Values.SelectMany(category => category).ToArray();
        if (allMvps.Length == 0)
            return null;

        var selected = allMvps[Random.Shared.Next(allMvps.Length)];
        return (selected.Key, selected.Value.Sound ?? string.Empty);
    }

    public static (string MvpName, string SoundPath)? GetRandomMvpForPlayer(ISwiftlyCore core, MVPConfig config, IPlayer player)
    {
        (string MvpName, string SoundPath)? selected = null;
        var count = 0;
        foreach (var entry in GetAvailableMvpsForPlayer(core, config, player))
            if (Random.Shared.Next(++count) == 0)
                selected = (entry.Key, entry.Value.Sound ?? string.Empty);
        return selected;
    }

    public static IEnumerable<KeyValuePair<string, MVP_Template>> GetAvailableMvpsForPlayer(ISwiftlyCore core, MVPConfig config, IPlayer player)
    {
        foreach (var category in config.MVPs.Values)
        {
            foreach (var mvp in category)
            {
                if (PlayerHasAccessToMvp(core, player, mvp.Value))
                {
                    yield return mvp;
                }
            }
        }
    }

    public static bool PlayerHasAccessToMvp(ISwiftlyCore core, IPlayer player, MVP_Template template)
    {
        ArgumentNullException.ThrowIfNull(core);
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(template);

        if (template.Permissions.Count == 0)
            return true;

        foreach (var permission in template.Permissions)
        {
            if (string.IsNullOrWhiteSpace(permission))
                continue;

            if (ulong.TryParse(permission, out var steamIdPermission))
            {
                if (player.SteamID == steamIdPermission)
                    return true;

                continue;
            }

            if (core.Permission.PlayerHasPermission(player.SteamID, permission))
                return true;
        }

        return false;
    }

    public static bool TryGetMvpTemplate(MVPConfig config, string mvpName, out MVP_Template mvpTemplate)
    {
        foreach (var category in config.MVPs.Values)
        {
            if (category.TryGetValue(mvpName, out mvpTemplate!))
            {
                return true;
            }
        }

        mvpTemplate = null!;
        return false;
    }

    public static string GetSoundPath(MVPConfig config, string mvpName)
    {
        if (string.IsNullOrWhiteSpace(mvpName))
            return string.Empty;

        if (TryGetMvpTemplate(config, mvpName, out var mvpTemplate))
        {
            return mvpTemplate.Sound ?? string.Empty;
        }

        return string.Empty;
    }

    public static void PlaySound(IAudioApi audioApi, IPlayer player, string sound, float volume)
    {
        ArgumentNullException.ThrowIfNull(audioApi);
        ArgumentNullException.ThrowIfNull(player);
        if (!player.IsValid || player.IsFakeClient || ClampVolume(volume) <= 0f || string.IsNullOrWhiteSpace(sound))
            return;

        var clampedVolume = ClampVolume(volume);
        var generation = _generation;
        Core.Scheduler.NextWorldUpdate(() =>
        {
            if (generation != _generation || !player.IsValid)
                return;

            PlaySoundSingleInternal(audioApi, player, sound, clampedVolume);
        });
    }

    public static void PlaySound(IAudioApi audioApi, IEnumerable<(IPlayer Player, float Volume)> listeners, string sound)
    {
        ArgumentNullException.ThrowIfNull(audioApi);
        ArgumentNullException.ThrowIfNull(listeners);
        if (string.IsNullOrWhiteSpace(sound))
            return;

        var listenerArray = listeners as (IPlayer Player, float Volume)[] ?? listeners.ToArray();
        if (listenerArray.Length == 0)
            return;

        var generation = _generation;
        Core.Scheduler.NextWorldUpdate(() =>
        {
            if (generation == _generation) PlaySoundManyInternal(audioApi, listenerArray, sound);
        });
    }

    private static void PlaySoundSingleInternal(IAudioApi audioApi, IPlayer player, string sound, float clampedVolume)
    {
        if (sound.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            var mp3Path = Path.IsPathRooted(sound) ? sound : Path.Combine(Core.PluginDataDirectory, sound);
            if (!File.Exists(mp3Path))
                return;

            var channelController = audioApi.UseChannel($"mvp_anthem.preview.{player.PlayerID}");
            var audioSource = audioApi.DecodeFromFile(mp3Path);
            channelController.SetSource(audioSource);
            channelController.SetVolume(player.PlayerID, clampedVolume);
            channelController.Play(player.PlayerID);
            return;
        }

        using var soundEvent = new SoundEvent
        {
            Name = sound,
            Volume = clampedVolume
        };
        soundEvent.Recipients.AddRecipient(player.PlayerID);
        soundEvent.Emit();
    }

    private static void PlaySoundManyInternal(IAudioApi audioApi, (IPlayer Player, float Volume)[] listeners, string sound)
    {
        var activeListeners = listeners.Where(static entry => entry.Player.IsValid && !entry.Player.IsFakeClient && ClampVolume(entry.Volume) > 0f).ToArray();
        if (activeListeners.Length == 0)
            return;

        if (sound.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            var mp3Path = Path.IsPathRooted(sound) ? sound : Path.Combine(Core.PluginDataDirectory, sound);
            if (!File.Exists(mp3Path))
                return;

            var channelController = audioApi.UseChannel("mvp_anthem.round_mvp");
            var audioSource = audioApi.DecodeFromFile(mp3Path);
            channelController.SetSource(audioSource);

            foreach (var (player, volume) in activeListeners)
            {
                channelController.SetVolume(player.PlayerID, ClampVolume(volume));
                channelController.Play(player.PlayerID);
            }

            return;
        }

        foreach (var (player, volume) in activeListeners)
        {
            using var soundEvent = new SoundEvent
            {
                Name = sound,
                Volume = ClampVolume(volume)
            };

            soundEvent.Recipients.AddRecipient(player.PlayerID);
            soundEvent.Emit();
        }
    }
    public static void RemoveInGameMvp(IPlayer player)
    {
        if (player.Controller is not CCSPlayerController controller)
            return;

        controller.MVPs = 0;
        controller.MVPsUpdated();

        controller.MusicKitMVPs = 0;
        controller.MusicKitMVPsUpdated();
    }
    public static void SendHTML(IPlayer player, string message, int duration)
    {
        if (!player.IsValid || player.IsFakeClient) return;
        if (duration <= 0 || string.IsNullOrWhiteSpace(message))
        {
            RemoveHtmlMessage(player.PlayerID);
            return;
        }
        SendMessages[player.PlayerID] = new HtmlSendEntry(player, player.SessionId, message,
            Environment.TickCount64 + (long)duration * 1000);
    }

    public static void RemoveHtmlMessage(int playerId) => SendMessages.Remove(playerId);
    public static void ClearHtmlMessages() => SendMessages.Clear();

    public static void RenderHtmlMessages()
    {
        if (SendMessages.Count == 0) return;
        var now = Environment.TickCount64;
        ExpiredMessages.Clear();
        foreach (var (id, entry) in SendMessages)
        {
            if (!entry.Player.IsValid || entry.Player.SessionId != entry.SessionId || now >= entry.ExpiresAt)
            {
                ExpiredMessages.Add(id);
                continue;
            }
            entry.Player.SendCenterHTML(entry.Message, 1);
        }
        foreach (var id in ExpiredMessages) SendMessages.Remove(id);
    }

    private sealed record HtmlSendEntry(IPlayer Player, ulong SessionId, string Message, long ExpiresAt);
}
