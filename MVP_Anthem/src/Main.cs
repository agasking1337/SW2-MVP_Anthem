using AudioApi;
using Cookies.Contract;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Commands;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.GameEvents;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.Plugins;

namespace MVP_Anthem;

[PluginMetadata(
    Id = "MVP_Anthem",
    Name = "MVP Anthem",
    Version = "1.0.0",
    Description = "MVP Plugin with fully customizable config"
)]
public sealed class Main(ISwiftlyCore core) : BasePlugin(core)
{
    private const string PlayerCookiesInterfaceKey = "Cookies.Player.v1";
    private const string PlayerCookiesInterfaceKeyLegacy = "Cookies.Player.V1";
    private const string AudioInterfaceKey = "audio";

    private ServiceProvider? _provider;
    public static new ISwiftlyCore Core { get; set; } = null!;
    private IPlayerCookiesAPIv1? Cookies { get; set; }
    private IAudioApi? AudioApi { get; set; }
    private MVPConfig Config { get; set; } = new MVPConfig();
    private MVPCookies? mvpCookies { get; set; }
    private MVPMenu? mvpMenu { get; set; }
    private List<Guid> mvpMenuCommandGuids { get; } = [];

    public override void UseSharedInterface(IInterfaceManager interfaceManager)
    {
        Cookies = ResolveSharedInterface<IPlayerCookiesAPIv1>(
            interfaceManager,
            [PlayerCookiesInterfaceKey, PlayerCookiesInterfaceKeyLegacy]
        );
        AudioApi = ResolveSharedInterface<IAudioApi>(interfaceManager, [AudioInterfaceKey]);
    }

    public override void OnSharedInterfaceInjected(IInterfaceManager interfaceManager)
    {
        UseSharedInterface(interfaceManager);
        InitializeRuntimeIfReady();
    }

    public override void Load(bool hotReload)
    {
        Core = base.Core;

        Core.Configuration.InitializeJsonWithModel<MVPConfig>("config.jsonc", "Main")
            .Configure(builder =>
            {
                builder.AddJsonFile("config.jsonc", false, true);
            });

        ServiceCollection services = new();
        services.AddSwiftly(Core)
                .AddOptionsWithValidateOnStart<MVPConfig>().BindConfiguration("Main");

        _provider = services.BuildServiceProvider();
        Config = _provider.GetRequiredService<IOptions<MVPConfig>>().Value;
        InitializeRuntimeIfReady();
    }

    private void InitializeRuntimeIfReady()
    {
        if (Cookies is null || AudioApi is null)
        {
            mvpCookies = null;
            mvpMenu = null;
            UnregisterConfiguredCommands();
            return;
        }

        mvpCookies = new MVPCookies(Cookies);
        mvpMenu = new MVPMenu(Core, Config, mvpCookies, AudioApi);
        RegisterConfiguredCommands();
    }

    private void RegisterConfiguredCommands()
    {
        foreach (var cmd in Config.Settings.MVPCommands)
        {
            if (string.IsNullOrWhiteSpace(cmd))
                continue;

            if (!Core.Command.IsCommandRegistered(cmd))
            {
                var guid = Core.Command.RegisterCommand(cmd, OnMvpCommand);
                mvpMenuCommandGuids.Add(guid);
            }
        }
    }

    private void UnregisterConfiguredCommands()
    {
        foreach (var commandGuid in mvpMenuCommandGuids)
        {
            Core.Command.UnregisterCommand(commandGuid);
        }
        mvpMenuCommandGuids.Clear();
    }

    private T? ResolveSharedInterface<T>(IInterfaceManager interfaceManager, IEnumerable<string> keys) where T : class
    {
        foreach (var key in keys.Distinct(StringComparer.Ordinal))
        {
            if (!interfaceManager.HasSharedInterface(key))
                continue;

            try
            {
                return interfaceManager.GetSharedInterface<T>(key);
            }
            catch
            {
                // Continue trying fallback keys.
            }
        }

        return null;
    }

    [GameEventHandler(HookMode.Post)]
    public HookResult OnPlayerConnectFull(EventPlayerConnectFull e)
    {
        if (mvpCookies is null)
            return HookResult.Continue;

        if (e.UserIdPlayer is not IPlayer player)
            return HookResult.Continue;

        var settings = mvpCookies.GetPlayerSettings(player);
        if (settings == null)
            return HookResult.Continue;

        settings.Player = player;
        var shouldSave = false;

        if (!settings.HadFirstConnect)
        {
            settings.HadFirstConnect = true;
            settings.Volume = Helper.ClampVolume(Config.Settings.DefaultVolume);
            shouldSave = true;

            if (Config.Settings.GiveRandomMVPOnFirstJoin)
            {
                var randomMvp = Helper.GetRandomMvpForPlayer(Core, Config, player);
                if (randomMvp.HasValue)
                {
                    settings.MVPName = randomMvp.Value.MvpName;
                    settings.SoundPath = randomMvp.Value.SoundPath;
                }
            }
        }
        else
        {
            var clampedVolume = Helper.ClampVolume(settings.Volume);
            if (!settings.Volume.Equals(clampedVolume))
            {
                settings.Volume = clampedVolume;
                shouldSave = true;
            }

            if (string.IsNullOrWhiteSpace(settings.SoundPath) && !string.IsNullOrWhiteSpace(settings.MVPName))
            {
                var mappedSoundPath = Helper.GetSoundPath(Config, settings.MVPName);
                if (!string.IsNullOrWhiteSpace(mappedSoundPath))
                {
                    settings.SoundPath = mappedSoundPath;
                    shouldSave = true;
                }
            }
        }

        if (shouldSave)
        {
            mvpCookies.SavePlayerSettings(settings);
        }

        return HookResult.Continue;
    }
    [GameEventHandler(HookMode.Pre)]
    public HookResult OnRoundMvp(EventRoundMvp e)
    {
        if (mvpCookies is null || AudioApi is null)
            return HookResult.Continue;

        if (e.UserIdPlayer is not IPlayer mvpPlayer)
            return HookResult.Continue;

        if (Config.Settings.RemovePlayerInGameMvp)
        {
            Helper.RemoveInGameMvp(mvpPlayer);
        }

        var settings = mvpCookies.GetPlayerSettings(mvpPlayer);
        if (settings == null)
            return HookResult.Continue;

        settings.Player = mvpPlayer;

        string mvpName;
        string soundPath;
        MVP_Template mvpTemplate;
        var shouldSaveSettings = false;

        if (settings.HasRandomMvp)
        {
            var randomMvp = Helper.GetRandomMvpForPlayer(Core, Config, mvpPlayer);
            if (!randomMvp.HasValue)
                return HookResult.Continue;

            mvpName = randomMvp.Value.MvpName;
            soundPath = randomMvp.Value.SoundPath;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(settings.MVPName))
                return HookResult.Continue;

            mvpName = settings.MVPName;
            soundPath = settings.SoundPath;
        }

        if (!Helper.TryGetMvpTemplate(Config, mvpName, out mvpTemplate))
            return HookResult.Continue;
        if (!Helper.PlayerHasAccessToMvp(Core, mvpPlayer, mvpTemplate))
            return HookResult.Continue;

        if (string.IsNullOrWhiteSpace(soundPath))
        {
            soundPath = mvpTemplate.Sound ?? string.Empty;
            if (!settings.HasRandomMvp && !string.IsNullOrWhiteSpace(soundPath))
            {
                settings.SoundPath = soundPath;
                shouldSaveSettings = true;
            }
        }

        if (shouldSaveSettings)
        {
            mvpCookies.SavePlayerSettings(settings);
        }

        var listeners = Core.PlayerManager.GetAllValidPlayers().ToArray();
        if (listeners.Length == 0)
            return HookResult.Continue;

        if (!string.IsNullOrWhiteSpace(soundPath))
        {
            var listenerVolumes = listeners
                .Select(listener =>
                {
                    var listenerSettings = mvpCookies.GetPlayerSettings(listener);
                    var listenerVolume = listenerSettings?.Volume ?? Config.Settings.DefaultVolume;
                    return (Player: listener, Volume: listenerVolume);
                })
                .ToArray();

            Helper.PlaySound(AudioApi, listenerVolumes, soundPath);
        }

        var mvpPlayerName = Helper.GetPlayerName(mvpPlayer);
        var chatKey = $"{mvpName}.chat";
        var htmlKey = $"{mvpName}.html";

        if (mvpTemplate.ShowChat)
        {
            foreach (var listener in listeners)
            {
                var localizer = Core.Translation.GetPlayerLocalizer(listener);
                var prefix = localizer["prefix"];
                var displayName = mvpTemplate.DisplayName;
                var chatText = localizer[chatKey, mvpPlayerName, displayName];
                listener.SendChat($"{prefix} {chatText}");
            }
        }

        if (mvpTemplate.ShowHtml)
        {
            var htmlDurationSeconds = Math.Max(0, Config.Settings.MVPMaxDuration);
            if (htmlDurationSeconds > 0)
            {
                foreach (var listener in listeners)
                {
                    var localizer = Core.Translation.GetPlayerLocalizer(listener);
                    var displayName = mvpTemplate.DisplayName;
                    var htmlText = localizer[htmlKey, mvpPlayerName, displayName];
                    Helper.SendHTML(listener, htmlText, htmlDurationSeconds);
                }
            }
        }

        return HookResult.Continue;
    }

    public override void Unload()
    {
        UnregisterConfiguredCommands();
    }

    private void OnMvpCommand(ICommandContext context)
    {
        if (!context.IsSentByPlayer || context.Sender is not IPlayer player || !player.IsValid)
        {
            context.Reply("This command can only be used by a player.");
            return;
        }

        if (mvpMenu is null)
        {
            context.Reply("MVP menu is unavailable: required shared interfaces are not ready.");
            return;
        }

        mvpMenu.OpenMainMenu(player);
    }
    [EventListener<EventDelegates.OnTick>]
    public void OnTick()
    {
        Helper.RenderHtmlMessages();
    }
}
