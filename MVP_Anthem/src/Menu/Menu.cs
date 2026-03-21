using AudioApi;
using SwiftlyS2.Core.Menus.OptionsBase;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Menus;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.Translation;

namespace MVP_Anthem;

public sealed class MVPMenu
{
    private const int MenuUpdateIntervalMs = 250;
    private const int MenuPauseIntervalMs = 250;

    private ISwiftlyCore Core { get; }
    private MVPConfig Config { get; }
    private MVPCookies MVP_Cookies { get; }
    private IAudioApi AudioApi { get; }

    public MVPMenu(ISwiftlyCore core, MVPConfig config, MVPCookies mvpCookies, IAudioApi audioApi)
    {
        Core = core;
        Config = config;
        MVP_Cookies = mvpCookies;
        AudioApi = audioApi;
    }

    public void OpenMainMenu(IPlayer player)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (!player.IsValid)
            return;

        var menu = BuildMainMenu(player);
        Core.MenusAPI.OpenMenuForPlayer(player, menu);
    }

    private IMenuAPI BuildMainMenu(IPlayer player)
    {
        var localizer = Core.Translation.GetPlayerLocalizer(player);
        var settings = MVP_Cookies.GetPlayerSettings(player) ?? new MVPCookies.PlayerSettings { Player = player };
        settings.Player = player;

        var builder = Core.MenusAPI.CreateBuilder();
        builder.Design.SetMenuTitle(GetMenuTitle(localizer["mvp.main_menu<title>"]));
        builder.SetPlayerFrozen(Config.Menu.FreezePlayer);
        if (Config.Menu.EnableSounds)
            builder.EnableSound();
        else
            builder.DisableSound();

        var activeMvpDisplay = GetCurrentMvpDisplayName(localizer, settings);
        var currentMvpOption = new TextMenuOption(
            localizer["mvp.main_menu.active_mvp<option>", activeMvpDisplay],
            MenuUpdateIntervalMs,
            MenuPauseIntervalMs
        )
        {
            Enabled = false,
            PlaySound = false
        };
        var volumeOptions = GetVolumeOptions();
        var currentVolumePercent = (int)MathF.Round(Helper.ClampVolume(settings.Volume) * 100f);
        var snappedCurrentVolume = FindClosestVolumeOption(volumeOptions, currentVolumePercent);
        builder.AddOption(currentMvpOption);

        var currentVolumeOption = new TextMenuOption(
            localizer["mvp.main_menu.current_volume<option>", snappedCurrentVolume],
            MenuUpdateIntervalMs,
            MenuPauseIntervalMs
        )
        {
            Enabled = false,
            PlaySound = false
        };
        builder.AddOption(currentVolumeOption);

        if (HasSelectedMvp(settings))
        {
            var removeMvpOption = new SubmenuMenuOption(
                localizer["mvp.main_menu.remove_mvp<option>"],
                () => BuildConfirmRemoveMvpMenu(player),
                MenuUpdateIntervalMs,
                MenuPauseIntervalMs
            );
            builder.AddOption(removeMvpOption);
        }

        var selectMvpOption = new SubmenuMenuOption(
            localizer["mvp.main_menu.select_mvp<option>"],
            () => BuildSelectMvpMenu(player),
            MenuUpdateIntervalMs,
            MenuPauseIntervalMs
        );
        builder.AddOption(selectMvpOption);

        var changeVolumeOption = new SubmenuMenuOption(
            localizer["mvp.main_menu.change_volume<option>"],
            () => BuildChangeVolumeMenu(player),
            MenuUpdateIntervalMs,
            MenuPauseIntervalMs
        );
        builder.AddOption(changeVolumeOption);

        return builder.Build();
    }

    private IMenuAPI BuildChangeVolumeMenu(IPlayer player)
    {
        var localizer = Core.Translation.GetPlayerLocalizer(player);
        var builder = Core.MenusAPI.CreateBuilder();
        builder.Design.SetMenuTitle(GetMenuTitle(localizer["mvp.main_menu.change_volume<option>"]));
        builder.SetPlayerFrozen(Config.Menu.FreezePlayer);
        if (Config.Menu.EnableSounds)
            builder.EnableSound();
        else
            builder.DisableSound();

        var volumeOptions = GetVolumeOptions();
        var settings = MVP_Cookies.GetPlayerSettings(player) ?? new MVPCookies.PlayerSettings { Player = player };
        var currentVolume = FindClosestVolumeOption(volumeOptions, (int)MathF.Round(Helper.ClampVolume(settings.Volume) * 100f));

        foreach (var volume in volumeOptions)
        {
            var label = localizer["mvp.volume_item<option>", volume];
            if (volume == currentVolume)
            {
                label = $"{label} <font color='green'>{localizer["mvp.current"]}</font>";
            }

            var volumeOption = new ButtonMenuOption(label, MenuUpdateIntervalMs, MenuPauseIntervalMs);
            volumeOption.Click += (_, args) =>
            {
                var selectedPlayer = args.Player;
                if (selectedPlayer is null || !selectedPlayer.IsValid)
                    return ValueTask.CompletedTask;

                var changedSettings = MVP_Cookies.GetPlayerSettings(selectedPlayer);
                if (changedSettings == null)
                    return ValueTask.CompletedTask;

                changedSettings.Player = selectedPlayer;
                changedSettings.Volume = volume / 100f;
                MVP_Cookies.SavePlayerSettings(changedSettings);

                SendLocalizedPrefixedChat(selectedPlayer, "volume.selected", volume);
                Core.Scheduler.NextTick(() => OpenMainMenu(selectedPlayer));
                return ValueTask.CompletedTask;
            };
            builder.AddOption(volumeOption);
        }

        return builder.Build();
    }

    private IMenuAPI BuildSelectMvpMenu(IPlayer player)
    {
        var localizer = Core.Translation.GetPlayerLocalizer(player);
        var builder = Core.MenusAPI.CreateBuilder();
        builder.Design.SetMenuTitle(GetMenuTitle(localizer["mvp.main_menu.select_mvp<option>"]));
        builder.SetPlayerFrozen(Config.Menu.FreezePlayer);
        if (Config.Menu.EnableSounds)
            builder.EnableSound();
        else
            builder.DisableSound();

        var hasAnyAccessibleMvp = Helper.GetAvailableMvpsForPlayer(Core, Config, player).Any();
        var randomMvpOption = new ButtonMenuOption(localizer["mvp.random<option>"], MenuUpdateIntervalMs, MenuPauseIntervalMs);
        randomMvpOption.Enabled = hasAnyAccessibleMvp;
        randomMvpOption.Click += (_, args) =>
        {
            var selectedPlayer = args.Player;
            if (selectedPlayer is null || !selectedPlayer.IsValid)
                return ValueTask.CompletedTask;

            var settings = MVP_Cookies.GetPlayerSettings(selectedPlayer);
            if (settings == null)
                return ValueTask.CompletedTask;

            var randomMvp = Helper.GetRandomMvpForPlayer(Core, Config, selectedPlayer);
            if (!randomMvp.HasValue)
                return ValueTask.CompletedTask;

            settings.Player = selectedPlayer;
            settings.HasRandomMvp = true;
            settings.MVPName = randomMvp.Value.MvpName;
            settings.SoundPath = randomMvp.Value.SoundPath;
            MVP_Cookies.SavePlayerSettings(settings);

            SendLocalizedPrefixedChat(selectedPlayer, localizer =>
                localizer["mvp.selcted", localizer["mvp.random<option>"]]
            );

            Core.Scheduler.NextTick(() => OpenMainMenu(selectedPlayer));
            return ValueTask.CompletedTask;
        };
        builder.AddOption(randomMvpOption);

        foreach (var category in Config.MVPs)
        {
            var accessibleMvps = category.Value
                .Where(mvp => Helper.PlayerHasAccessToMvp(Core, player, mvp.Value))
                .ToArray();
            if (accessibleMvps.Length == 0)
                continue;

            var categoryName = localizer[category.Key];
            var categorySubMenuOption = new SubmenuMenuOption(
                categoryName,
                () => BuildCategoryMvpMenu(player, category.Key, accessibleMvps),
                MenuUpdateIntervalMs,
                MenuPauseIntervalMs
            );
            builder.AddOption(categorySubMenuOption);
        }

        return builder.Build();
    }

    private IMenuAPI BuildCategoryMvpMenu(
        IPlayer player,
        string categoryKey,
        IReadOnlyCollection<KeyValuePair<string, MVP_Template>> accessibleMvps
    )
    {
        var localizer = Core.Translation.GetPlayerLocalizer(player);
        var builder = Core.MenusAPI.CreateBuilder();
        builder.Design.SetMenuTitle(GetMenuTitle(localizer[categoryKey]));
        builder.SetPlayerFrozen(Config.Menu.FreezePlayer);
        if (Config.Menu.EnableSounds)
            builder.EnableSound();
        else
            builder.DisableSound();

        foreach (var mvp in accessibleMvps)
        {
            var mvpKey = mvp.Key;
            var mvpTemplate = mvp.Value;
            var mvpDisplayName = mvpTemplate.DisplayName;

            var mvpSubMenuOption = new SubmenuMenuOption(
                mvpDisplayName,
                () => BuildMvpActionsMenu(player, mvpKey, mvpTemplate),
                MenuUpdateIntervalMs,
                MenuPauseIntervalMs
            );
            builder.AddOption(mvpSubMenuOption);
        }

        return builder.Build();
    }

    private IMenuAPI BuildMvpActionsMenu(IPlayer player, string mvpKey, MVP_Template mvpTemplate)
    {
        var localizer = Core.Translation.GetPlayerLocalizer(player);
        var builder = Core.MenusAPI.CreateBuilder();
        builder.Design.SetMenuTitle(GetMenuTitle(mvpTemplate.DisplayName));
        builder.SetPlayerFrozen(Config.Menu.FreezePlayer);
        if (Config.Menu.EnableSounds)
            builder.EnableSound();
        else
            builder.DisableSound();

        var selectOption = new ButtonMenuOption(localizer["mvp.select_this<option>"], MenuUpdateIntervalMs, MenuPauseIntervalMs);
        selectOption.Click += (_, args) =>
        {
            var selectedPlayer = args.Player;
            if (selectedPlayer is null || !selectedPlayer.IsValid)
                return ValueTask.CompletedTask;

            var settings = MVP_Cookies.GetPlayerSettings(selectedPlayer);
            if (settings == null)
                return ValueTask.CompletedTask;

            settings.Player = selectedPlayer;
            settings.HasRandomMvp = false;
            settings.MVPName = mvpKey;
            settings.SoundPath = mvpTemplate.Sound ?? string.Empty;
            MVP_Cookies.SavePlayerSettings(settings);

            SendLocalizedPrefixedChat(selectedPlayer, localizer =>
                localizer["mvp.selcted", localizer[mvpTemplate.DisplayName]]
            );

            Core.Scheduler.NextTick(() => OpenMainMenu(selectedPlayer));
            return ValueTask.CompletedTask;
        };
        builder.AddOption(selectOption);

        if (mvpTemplate.EnablePreview)
        {
            var previewOption = new ButtonMenuOption(localizer["mvp.preview<option>"], MenuUpdateIntervalMs, MenuPauseIntervalMs)
            {
                CloseAfterClick = false
            };
            previewOption.Click += (_, args) =>
            {
                var previewPlayer = args.Player;
                if (previewPlayer is null || !previewPlayer.IsValid)
                    return ValueTask.CompletedTask;

                var previewSettings = MVP_Cookies.GetPlayerSettings(previewPlayer);
                var previewVolume = previewSettings?.Volume ?? Config.Settings.DefaultVolume;
                var previewSound = mvpTemplate.Sound ?? string.Empty;
                Helper.PlaySound(AudioApi, previewPlayer, previewSound, previewVolume);
                return ValueTask.CompletedTask;
            };
            builder.AddOption(previewOption);
        }

        return builder.Build();
    }

    private IMenuAPI BuildConfirmRemoveMvpMenu(IPlayer player)
    {
        var localizer = Core.Translation.GetPlayerLocalizer(player);
        var builder = Core.MenusAPI.CreateBuilder();
        builder.Design.SetMenuTitle(GetMenuTitle(localizer["mvp.remove.confirm<title>"]));
        builder.SetPlayerFrozen(Config.Menu.FreezePlayer);
        if (Config.Menu.EnableSounds)
            builder.EnableSound();
        else
            builder.DisableSound();

        var confirmOption = new ButtonMenuOption(localizer["mvp.remove.confirm_yes<option>"], MenuUpdateIntervalMs, MenuPauseIntervalMs);
        confirmOption.Click += (_, args) =>
        {
            var selectedPlayer = args.Player;
            if (selectedPlayer is null || !selectedPlayer.IsValid)
                return ValueTask.CompletedTask;

            var settings = MVP_Cookies.GetPlayerSettings(selectedPlayer);
            if (settings == null)
                return ValueTask.CompletedTask;

            settings.Player = selectedPlayer;
            settings.HasRandomMvp = false;
            settings.MVPName = string.Empty;
            settings.SoundPath = string.Empty;
            MVP_Cookies.SavePlayerSettings(settings);

            SendLocalizedPrefixedChat(selectedPlayer, "mvp.removed");

            Core.Scheduler.NextTick(() => OpenMainMenu(selectedPlayer));
            return ValueTask.CompletedTask;
        };
        builder.AddOption(confirmOption);

        var cancelOption = new ButtonMenuOption(localizer["mvp.remove.cancel<option>"], MenuUpdateIntervalMs, MenuPauseIntervalMs);
        cancelOption.Click += (_, args) =>
        {
            if (args.Player is null || !args.Player.IsValid)
                return ValueTask.CompletedTask;

            Core.Scheduler.NextTick(() => OpenMainMenu(args.Player));
            return ValueTask.CompletedTask;
        };
        builder.AddOption(cancelOption);

        return builder.Build();
    }

    private string GetMenuTitle(string plainTitle)
    {
        if (!Config.Menu.GradientTitleColor)
            return plainTitle;

        return HtmlGradient.GenerateGradientText(plainTitle, "#F59E0B", "#3B82F6");
    }

    private static bool HasSelectedMvp(MVPCookies.PlayerSettings settings)
    {
        return settings.HasRandomMvp || !string.IsNullOrWhiteSpace(settings.MVPName);
    }

    private static int[] GetVolumeOptionsFallback()
    {
        return [0, 10, 20, 40, 60, 80, 100];
    }

    private int[] GetVolumeOptions()
    {
        var configuredOptions = Config.Menu.VolumeOptions
            .Distinct()
            .Where(value => value >= 0 && value <= 100)
            .OrderBy(value => value)
            .ToArray();

        return configuredOptions.Length > 0 ? configuredOptions : GetVolumeOptionsFallback();
    }

    private static int FindClosestVolumeOption(int[] options, int value)
    {
        var best = options[0];
        var bestDistance = Math.Abs(value - best);

        for (var i = 1; i < options.Length; i++)
        {
            var candidate = options[i];
            var distance = Math.Abs(value - candidate);
            if (distance < bestDistance)
            {
                best = candidate;
                bestDistance = distance;
            }
        }

        return best;
    }

    private string GetCurrentMvpDisplayName(ILocalizer localizer, MVPCookies.PlayerSettings settings)
    {
        if (settings.HasRandomMvp)
            return localizer["mvp.random<option>"];

        if (string.IsNullOrWhiteSpace(settings.MVPName))
            return localizer["mvp.none"];

        if (Helper.TryGetMvpTemplate(Config, settings.MVPName, out var template) &&
            Helper.PlayerHasAccessToMvp(Core, settings.Player, template))
            return template.DisplayName;

        return localizer["mvp.none"];
    }

    private void SendLocalizedPrefixedChat(IPlayer player, string key, params object[] args)
    {
        SendLocalizedPrefixedChat(player, localizer => localizer[key, args]);
    }

    private void SendLocalizedPrefixedChat(IPlayer player, Func<ILocalizer, string> messageBuilder)
    {
        Core.Scheduler.NextWorldUpdate(() =>
        {
            if (player is null || !player.IsValid)
                return;

            var localizer = Core.Translation.GetPlayerLocalizer(player);
            var prefix = localizer["prefix"];
            var message = messageBuilder(localizer);
            player.SendChat($"{prefix} {message}");
        });
    }
}
