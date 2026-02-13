using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Text.Json;
using System.Text.Json.Serialization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Menu;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.Modules.Admin;
using Microsoft.Extensions.Logging;

namespace VoteSystem;

// =================== КОНФИГ ===================

public class VoteConfig
{
    [JsonPropertyName("vote_duration_seconds")]
    public int VoteDurationSeconds { get; set; } = 30;

    [JsonPropertyName("vote_cooldown_seconds")]
    public int VoteCooldownSeconds { get; set; } = 60;

    [JsonPropertyName("vote_pass_percent")]
    public float VotePassPercent { get; set; } = 0.55f;

    [JsonPropertyName("min_players_to_vote")]
    public int MinPlayersToVote { get; set; } = 3;

    [JsonPropertyName("ban_duration_minutes")]
    public int BanDurationMinutes { get; set; } = 30;

    [JsonPropertyName("gag_duration_seconds")]
    public int GagDurationSeconds { get; set; } = 600;

    [JsonPropertyName("mute_duration_seconds")]
    public int MuteDurationSeconds { get; set; } = 600;

    [JsonPropertyName("allow_votekick")]
    public bool AllowVoteKick { get; set; } = true;

    [JsonPropertyName("allow_voteban")]
    public bool AllowVoteBan { get; set; } = true;

    [JsonPropertyName("allow_votegag")]
    public bool AllowVoteGag { get; set; } = true;

    [JsonPropertyName("allow_votemute")]
    public bool AllowVoteMute { get; set; } = true;

    [JsonPropertyName("ban_system_type")]
    public string BanSystemType { get; set; } = "both";

    [JsonPropertyName("protect_admins")]
    public bool ProtectAdmins { get; set; } = true;

    [JsonPropertyName("admin_permission_cancel")]
    public string AdminPermissionCancel { get; set; } = "@css/kick";

    [JsonPropertyName("admin_protection_flag")]
    public string AdminProtectionFlag { get; set; } = "@css/kick";

    [JsonPropertyName("show_midvote_status")]
    public bool ShowMidvoteStatus { get; set; } = true;

    [JsonPropertyName("allow_early_finish")]
    public bool AllowEarlyFinish { get; set; } = true;

    [JsonPropertyName("menu_type")]
    public int MenuType { get; set; } = 3;

    [JsonPropertyName("freeze_on_menu")]
    public bool FreezeOnMenu { get; set; } = true;
}

// =================== ДАННЫЕ ГОЛОСОВАНИЯ ===================

public class VoteData
{
    public string InitiatorName { get; set; } = "";
    public ulong InitiatorSteamId { get; set; }
    public string TargetName { get; set; } = "";
    public int TargetUserId { get; set; }
    public ulong TargetSteamId { get; set; }
    public int TargetSlot { get; set; }
    public VoteType Type { get; set; }
    public string Reason { get; set; } = "";
    public HashSet<ulong> VotesYes { get; set; } = new();
    public HashSet<ulong> VotesNo { get; set; } = new();
    public DateTime StartTime { get; set; }
    public CounterStrikeSharp.API.Modules.Timers.Timer? Timer { get; set; }
}

// Gag = блокировка ТЕКСТОВОГО чата (css_gag / css_addgag)
// Mute = блокировка ГОЛОСОВОГО чата (css_mute / css_addmute)
public enum VoteType
{
    Kick,
    Ban,
    Gag,
    Mute
}

// =================== ПЛАГИН ===================

[MinimumApiVersion(80)]
public class VoteSystem : BasePlugin
{
    public override string ModuleName => "VoteSystem";
    public override string ModuleVersion => "1.5.0";
    public override string ModuleAuthor => "broo";
    public override string ModuleDescription => "Vote Kick/Ban/Gag/Mute system for CS2";

    private VoteData? _activeVote;
    private DateTime _lastVoteEnd = DateTime.MinValue;
    private VoteConfig _config = new();
    private string _configPath = "";
    private Dictionary<ulong, (CCSPlayerController target, VoteType type)> _pendingReason = new();
    private HashSet<int> _frozenPlayers = new();

    // MenuManager через рефлексию (обход [Obsolete(error=true)])
    private object? _menuApi;
    private System.Reflection.MethodInfo? _newMenuMethod;
    private System.Reflection.MethodInfo? _addOptionMethod;
    private System.Reflection.MethodInfo? _openMethod;
    private object? _buttonMenuType; // enum value MenuType.ButtonMenu

    public override void Load(bool hotReload)
    {
        _configPath = Path.Combine(ModuleDirectory, "config.json");
        LoadConfig();

        AddCommandListener("say", OnPlayerChat);
        AddCommandListener("say_team", OnPlayerChat);

        RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);

        Logger.LogInformation($"[VoteSystem] v{ModuleVersion} loaded! Menu type: {_config.MenuType}");
    }

    public override void OnAllPluginsLoaded(bool hotReload)
    {
        if (_config.MenuType != 3) return;

        try
        {
            System.Reflection.Assembly? apiAsm = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.GetName().Name == "MenuManagerApi")
                    apiAsm = asm;
            }

            if (apiAsm == null) return;

            var iMenuApiType = apiAsm.GetType("MenuManager.IMenuApi");
            if (iMenuApiType == null) return;

            var capType = typeof(CounterStrikeSharp.API.Core.Capabilities.PluginCapability<>).MakeGenericType(iMenuApiType);
            var capInstance = Activator.CreateInstance(capType, "menu:nfcore");
            var getMethod = capType.GetMethod("Get");
            _menuApi = getMethod?.Invoke(capInstance, null);
            if (_menuApi == null) return;

            var menuTypeEnum = apiAsm.GetType("MenuManager.MenuType");
            if (menuTypeEnum != null)
                _buttonMenuType = Enum.Parse(menuTypeEnum, "ButtonMenu");

            _newMenuMethod = _menuApi.GetType().GetMethod("NewMenuForcetype");
            if (_newMenuMethod != null)
            {
                var iMenuType = _newMenuMethod.ReturnType;
                _addOptionMethod = iMenuType.GetMethod("AddMenuOption");
                _openMethod = iMenuType.GetMethod("Open");
            }

            if (_newMenuMethod != null && _addOptionMethod != null && _openMethod != null)
                Logger.LogInformation("[VoteSystem] MenuManager API connected!");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[VoteSystem] MenuManager init error");
            _menuApi = null;
        }
    }

    private HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player != null)
        {
            _frozenPlayers.Remove(player.Slot);
            _pendingReason.Remove(player.SteamID);
        }
        return HookResult.Continue;
    }

    // =================== ЗАМОРОЗКА ===================

    private void FreezePlayer(CCSPlayerController player)
    {
        if (!_config.FreezeOnMenu) return;
        if (player.PlayerPawn?.Value == null) return;

        player.PlayerPawn.Value.MoveType = MoveType_t.MOVETYPE_OBSOLETE;
        player.PlayerPawn.Value.ActualMoveType = MoveType_t.MOVETYPE_OBSOLETE;
        player.PlayerPawn.Value.VelocityModifier = 0f;
        _frozenPlayers.Add(player.Slot);
    }

    private void UnfreezePlayer(CCSPlayerController player)
    {
        if (!_frozenPlayers.Contains(player.Slot)) return;
        if (player.PlayerPawn?.Value == null) return;

        player.PlayerPawn.Value.MoveType = MoveType_t.MOVETYPE_WALK;
        player.PlayerPawn.Value.ActualMoveType = MoveType_t.MOVETYPE_WALK;
        player.PlayerPawn.Value.VelocityModifier = 1f;
        _frozenPlayers.Remove(player.Slot);
    }

    private void UnfreezeAll()
    {
        foreach (var slot in _frozenPlayers.ToList())
        {
            var player = Utilities.GetPlayerFromSlot(slot);
            if (player != null && player.IsValid && player.PlayerPawn?.Value != null)
            {
                player.PlayerPawn.Value.MoveType = MoveType_t.MOVETYPE_WALK;
                player.PlayerPawn.Value.ActualMoveType = MoveType_t.MOVETYPE_WALK;
                player.PlayerPawn.Value.VelocityModifier = 1f;
            }
        }
        _frozenPlayers.Clear();
    }

    // =================== КОНФИГ ===================

    private void LoadConfig()
    {
        try
        {
            if (File.Exists(_configPath))
            {
                string raw = File.ReadAllText(_configPath);
                string json = StripJsonComments(raw);
                _config = JsonSerializer.Deserialize<VoteConfig>(json) ?? new VoteConfig();
                Console.WriteLine("[VoteSystem] Config loaded.");
            }
            else
            {
                _config = new VoteConfig();
                SaveConfig();
                Console.WriteLine("[VoteSystem] Default config created.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VoteSystem] Config error: {ex.Message}. Using defaults.");
            _config = new VoteConfig();
        }
    }

    private static string StripJsonComments(string input)
    {
        var lines = input.Split('\n');
        var result = new List<string>();
        foreach (var line in lines)
        {
            if (line.TrimStart().StartsWith("//")) continue;
            result.Add(line);
        }
        return string.Join('\n', result);
    }

    private void SaveConfig()
    {
        try
        {
            string content = @"// ========================================
// VoteSystem v1.5 — Конфигурация
// ========================================
// После изменения: !votereload или рестарт
// ========================================
{
    // Длительность голосования в секундах
    ""vote_duration_seconds"": " + _config.VoteDurationSeconds + @",

    // Кулдаун между голосованиями в секундах
    ""vote_cooldown_seconds"": " + _config.VoteCooldownSeconds + @",

    // Процент голосов ""ЗА"" от общего числа игроков (0.55 = 55%)
    ""vote_pass_percent"": " + _config.VotePassPercent.ToString(System.Globalization.CultureInfo.InvariantCulture) + @",

    // Минимальное количество игроков для голосования
    ""min_players_to_vote"": " + _config.MinPlayersToVote + @",

    // Длительность бана в минутах
    ""ban_duration_minutes"": " + _config.BanDurationMinutes + @",

    // Длительность блокировки ТЕКСТОВОГО чата в секундах (gag)
    ""gag_duration_seconds"": " + _config.GagDurationSeconds + @",

    // Длительность блокировки ГОЛОСОВОГО чата в секундах (mute)
    ""mute_duration_seconds"": " + _config.MuteDurationSeconds + @",

    // Разрешить голосование за кик (!votekick)
    ""allow_votekick"": " + BoolToJson(_config.AllowVoteKick) + @",

    // Разрешить голосование за бан (!voteban)
    ""allow_voteban"": " + BoolToJson(_config.AllowVoteBan) + @",

    // Разрешить голосование за блокировку чата (!votegag)
    ""allow_votegag"": " + BoolToJson(_config.AllowVoteGag) + @",

    // Разрешить голосование за блокировку войса (!votemute)
    ""allow_votemute"": " + BoolToJson(_config.AllowVoteMute) + @",

    // Система наказаний:
    //   ""as""   — Admin System (mm_ban, mm_mute, mm_gag)
    //   ""iks""  — IksAdmin (css_ban, css_mute, css_gag для онлайн)
    //   ""both"" — обе системы
    ""ban_system_type"": """ + _config.BanSystemType + @""",

    // Защита администраторов от голосований
    ""protect_admins"": " + BoolToJson(_config.ProtectAdmins) + @",

    // Флаг для отмены голосования (!votecancel)
    ""admin_permission_cancel"": """ + _config.AdminPermissionCancel + @""",

    // Флаг защиты от голосований
    ""admin_protection_flag"": """ + _config.AdminProtectionFlag + @""",

    // Промежуточный результат
    ""show_midvote_status"": " + BoolToJson(_config.ShowMidvoteStatus) + @",

    // Досрочное завершение
    ""allow_early_finish"": " + BoolToJson(_config.AllowEarlyFinish) + @",

    // Тип меню:
    //   0 = ChatMenu
    //   1 = ConsoleMenu
    //   2 = HtmlMenu (по центру)
    //   3 = ButtonMenu (WASD + E/R, MenuManager)
    ""menu_type"": " + _config.MenuType + @",

    // Замораживать игрока при открытии меню
    ""freeze_on_menu"": " + BoolToJson(_config.FreezeOnMenu) + @"
}";
            File.WriteAllText(_configPath, content);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VoteSystem] Failed to save config: {ex.Message}");
        }
    }

    private static string BoolToJson(bool value) => value ? "true" : "false";

    // =================== МЕНЮ ===================

    private void OpenMenu(CCSPlayerController player, string title, List<(string text, Action<CCSPlayerController> action)> items, bool isLastMenu = false, Action<CCSPlayerController>? backAction = null)
    {
        FreezePlayer(player);

        // Оборачиваем каждый action для разморозки при финальном выборе
        List<(string text, Action<CCSPlayerController> action)> wrappedItems;

        if (isLastMenu)
        {
            // Последнее меню — размораживаем после выбора
            wrappedItems = items.Select(item =>
            {
                var act = item.action;
                return (item.text, (Action<CCSPlayerController>)(p =>
                {
                    UnfreezePlayer(p);
                    act(p);
                }));
            }).ToList();
        }
        else
        {
            // Промежуточное меню — не размораживаем (следующее меню откроется)
            wrappedItems = items;
        }

        // MenuManager ButtonMenu (WASD + E/R)
        if (_config.MenuType == 3 && _menuApi != null && _newMenuMethod != null && _addOptionMethod != null && _openMethod != null)
        {
            try
            {
                // NewMenuForcetype(string title, MenuType type, Action<CCSPlayerController> backAction)
                // backAction — MenuManager автоматически добавит "← Назад" и A/D пагинацию
                var menu = _newMenuMethod.Invoke(_menuApi, new object?[] { title, _buttonMenuType!, backAction });
                if (menu == null) goto fallback;

                // Параметры AddMenuOption
                var addParams = _addOptionMethod.GetParameters();
                var callbackType = addParams[1].ParameterType; // Action<CCSPlayerController, IMenuOption>
                var genericArgs = callbackType.GetGenericArguments();

                foreach (var item in wrappedItems)
                {
                    var act = item.action;

                    // Создаём (CCSPlayerController p, IMenuOption opt) => act(p) через Expression
                    var pParam = Expression.Parameter(typeof(CCSPlayerController), "p");
                    var optParam = Expression.Parameter(genericArgs[1], "opt");
                    var actConst = Expression.Constant(act);
                    var invokeCall = Expression.Call(actConst, act.GetType().GetMethod("Invoke")!, pParam);
                    var lambda = Expression.Lambda(callbackType, invokeCall, pParam, optParam);
                    var callback = lambda.Compile();

                    var addArgs = new object?[addParams.Length];
                    addArgs[0] = item.text;
                    addArgs[1] = callback;
                    for (int i = 2; i < addParams.Length; i++)
                        addArgs[i] = addParams[i].HasDefaultValue ? addParams[i].DefaultValue : false;

                    _addOptionMethod.Invoke(menu, addArgs);
                }

                _openMethod.Invoke(menu, new object[] { player });
                return;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "[VoteSystem] MenuManager OpenMenu error");
            }
        }

        fallback:

        // Для CSS меню добавляем "← Назад" вручную
        if (backAction != null)
        {
            var back = backAction;
            wrappedItems.Insert(0, ("← Назад", p => back(p)));
        }

        // Встроенные CSS меню
        switch (_config.MenuType)
        {
            case 1:
            {
                var menu = new ConsoleMenu(title);
                foreach (var item in wrappedItems)
                {
                    var act = item.action;
                    menu.AddMenuOption(item.text, (p, opt) => act(p));
                }
                CounterStrikeSharp.API.Modules.Menu.MenuManager.OpenConsoleMenu(player, menu);
                break;
            }
            case 2:
            case 3: // fallback если MenuManager не загружен
            {
                var menu = new CenterHtmlMenu(title, this);
                foreach (var item in wrappedItems)
                {
                    var act = item.action;
                    menu.AddMenuOption(item.text, (p, opt) => act(p));
                }
                CounterStrikeSharp.API.Modules.Menu.MenuManager.OpenCenterHtmlMenu(this, player, menu);
                break;
            }
            default:
            {
                var menu = new ChatMenu(title);
                foreach (var item in wrappedItems)
                {
                    var act = item.action;
                    menu.AddMenuOption(item.text, (p, opt) => act(p));
                }
                CounterStrikeSharp.API.Modules.Menu.MenuManager.OpenChatMenu(player, menu);
                break;
            }
        }
    }

    private void OpenMainMenu(CCSPlayerController caller)
    {
        var items = new List<(string, Action<CCSPlayerController>)>();

        if (_config.AllowVoteKick)
            items.Add(("Кикнуть игрока", p => OpenPlayerListMenu(p, VoteType.Kick)));
        if (_config.AllowVoteBan)
            items.Add(("Забанить игрока", p => OpenPlayerListMenu(p, VoteType.Ban)));
        if (_config.AllowVoteGag)
            items.Add(("Заблокировать чат", p => OpenPlayerListMenu(p, VoteType.Gag)));
        if (_config.AllowVoteMute)
            items.Add(("Заблокировать войс", p => OpenPlayerListMenu(p, VoteType.Mute)));

        OpenMenu(caller, "★ Голосование", items);
    }

    private void OpenPlayerListMenu(CCSPlayerController caller, VoteType type)
    {
        string? err = PreCheckVote(caller);
        if (err != null) { UnfreezePlayer(caller); caller.PrintToChat(err); return; }

        var players = GetOnlinePlayers()
            .Where(p => p.SteamID != caller.SteamID)
            .Where(p => !_config.ProtectAdmins || !AdminManager.PlayerHasPermissions(p, _config.AdminProtectionFlag))
            .OrderBy(p => p.PlayerName)
            .ToList();

        if (players.Count == 0)
        {
            UnfreezePlayer(caller);
            caller.PrintToChat($" {ChatColors.Red}[Vote]{ChatColors.Default} Нет доступных игроков.");
            return;
        }

        var items = new List<(string, Action<CCSPlayerController>)>();
        foreach (var player in players)
        {
            ulong sid = player.SteamID;
            string name = player.PlayerName;
            items.Add((name, initiator =>
            {
                var target = FindPlayerBySteamId(sid);
                if (target == null || !target.IsValid)
                {
                    UnfreezePlayer(initiator);
                    initiator.PrintToChat($" {ChatColors.Red}[Vote]{ChatColors.Default} Игрок покинул сервер.");
                    return;
                }
                OpenReasonMenu(initiator, target, type);
            }));
        }

        string typeStr = GetVoteTypeName(type);
        // Назад → главное меню
        OpenMenu(caller, $"★ {typeStr} — Игрок", items, backAction: p => OpenMainMenu(p));
    }

    private void OpenReasonMenu(CCSPlayerController caller, CCSPlayerController target, VoteType type)
    {
        string? err = PreCheckVote(caller);
        if (err != null) { UnfreezePlayer(caller); caller.PrintToChat(err); return; }

        ulong targetSid = target.SteamID;
        string targetName = target.PlayerName;

        string[] reasons = type switch
        {
            VoteType.Ban => new[] { "Читы", "Токсик", "Оскорбление", "Слив раунда", "Своя причина" },
            VoteType.Gag => new[] { "Флуд в чате", "Оскорбление", "Реклама", "Спам", "Своя причина" },
            VoteType.Mute => new[] { "Флуд в микро", "Оскорбление", "Громкая музыка", "Крик", "Своя причина" },
            _ => new[] { "АФК", "Токсик", "Слив раунда", "Мешает команде", "Своя причина" }
        };

        var items = new List<(string, Action<CCSPlayerController>)>();
        foreach (var reason in reasons)
        {
            string r = reason;
            items.Add((r, initiator =>
            {
                var t = FindPlayerBySteamId(targetSid);
                if (t == null || !t.IsValid)
                {
                    initiator.PrintToChat($" {ChatColors.Red}[Vote]{ChatColors.Default} Игрок покинул сервер.");
                    return;
                }
                if (r == "Своя причина")
                {
                    initiator.PrintToChat($" {ChatColors.Yellow}[Vote]{ChatColors.Default} Напишите причину в чат:");
                    _pendingReason[initiator.SteamID] = (t, type);
                    return;
                }
                TryStartVote(initiator, t, type, r);
            }));
        }

        string typeStr = GetVoteTypeName(type);
        // Назад → список игроков
        OpenMenu(caller, $"★ {typeStr} {targetName} — Причина", items, isLastMenu: true, backAction: p => OpenPlayerListMenu(p, type));
    }

    // =================== ЧАТ ПЕРЕХВАТ ===================

    private HookResult OnPlayerChat(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !player.IsValid) return HookResult.Continue;
        if (!_pendingReason.TryGetValue(player.SteamID, out var pending))
            return HookResult.Continue;

        string message = info.GetArg(1).Trim();
        if (string.IsNullOrEmpty(message) || message.StartsWith("!") || message.StartsWith("/"))
            return HookResult.Continue;

        _pendingReason.Remove(player.SteamID);

        var target = pending.target;
        if (target == null || !target.IsValid)
        {
            player.PrintToChat($" {ChatColors.Red}[Vote]{ChatColors.Default} Игрок покинул сервер.");
            return HookResult.Handled;
        }

        TryStartVote(player, target, pending.type, message);
        return HookResult.Handled;
    }

    // =================== КОМАНДЫ ===================

    [ConsoleCommand("css_vote", "Open vote menu")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnVoteMenuCommand(CCSPlayerController? caller, CommandInfo command)
    {
        if (caller == null || !caller.IsValid) return;
        OpenMainMenu(caller);
    }

    [ConsoleCommand("css_votekick", "Vote to kick")]
    [CommandHelper(usage: "[player] [reason]", whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnVoteKickCommand(CCSPlayerController? caller, CommandInfo command)
    {
        if (caller == null || !caller.IsValid) return;
        if (!_config.AllowVoteKick) { caller.PrintToChat($" {ChatColors.Red}[Vote]{ChatColors.Default} Голосование за кик отключено."); return; }
        if (command.ArgCount < 2) { OpenPlayerListMenu(caller, VoteType.Kick); return; }
        HandleVoteCommand(caller, command, VoteType.Kick);
    }

    [ConsoleCommand("css_voteban", "Vote to ban")]
    [CommandHelper(usage: "[player] [reason]", whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnVoteBanCommand(CCSPlayerController? caller, CommandInfo command)
    {
        if (caller == null || !caller.IsValid) return;
        if (!_config.AllowVoteBan) { caller.PrintToChat($" {ChatColors.Red}[Vote]{ChatColors.Default} Голосование за бан отключено."); return; }
        if (command.ArgCount < 2) { OpenPlayerListMenu(caller, VoteType.Ban); return; }
        HandleVoteCommand(caller, command, VoteType.Ban);
    }

    [ConsoleCommand("css_votegag", "Vote to gag (text chat)")]
    [CommandHelper(usage: "[player] [reason]", whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnVoteGagCommand(CCSPlayerController? caller, CommandInfo command)
    {
        if (caller == null || !caller.IsValid) return;
        if (!_config.AllowVoteGag) { caller.PrintToChat($" {ChatColors.Red}[Vote]{ChatColors.Default} Голосование за блокировку чата отключено."); return; }
        if (command.ArgCount < 2) { OpenPlayerListMenu(caller, VoteType.Gag); return; }
        HandleVoteCommand(caller, command, VoteType.Gag);
    }

    [ConsoleCommand("css_votemute", "Vote to mute (voice)")]
    [CommandHelper(usage: "[player] [reason]", whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnVoteMuteCommand(CCSPlayerController? caller, CommandInfo command)
    {
        if (caller == null || !caller.IsValid) return;
        if (!_config.AllowVoteMute) { caller.PrintToChat($" {ChatColors.Red}[Vote]{ChatColors.Default} Голосование за блокировку войса отключено."); return; }
        if (command.ArgCount < 2) { OpenPlayerListMenu(caller, VoteType.Mute); return; }
        HandleVoteCommand(caller, command, VoteType.Mute);
    }

    [ConsoleCommand("css_y", "Vote yes")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnVoteYes(CCSPlayerController? caller, CommandInfo command) => CastVote(caller, true);

    [ConsoleCommand("css_n", "Vote no")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnVoteNo(CCSPlayerController? caller, CommandInfo command) => CastVote(caller, false);

    [ConsoleCommand("css_votecancel", "Cancel vote")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnVoteCancel(CCSPlayerController? caller, CommandInfo command)
    {
        if (caller == null || !caller.IsValid) return;
        if (!AdminManager.PlayerHasPermissions(caller, _config.AdminPermissionCancel))
        { caller.PrintToChat($" {ChatColors.Red}[Vote]{ChatColors.Default} Нет доступа."); return; }
        if (_activeVote == null)
        { caller.PrintToChat($" {ChatColors.Red}[Vote]{ChatColors.Default} Нет активного голосования."); return; }
        CancelVote("Отменено администратором");
    }

    [ConsoleCommand("css_votereload", "Reload config")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void OnVoteReload(CCSPlayerController? caller, CommandInfo command)
    {
        if (caller != null && !AdminManager.PlayerHasPermissions(caller, "@css/root"))
        { caller.PrintToChat($" {ChatColors.Red}[Vote]{ChatColors.Default} Нет доступа."); return; }
        LoadConfig();
        if (caller != null) caller.PrintToChat($" {ChatColors.Green}[Vote]{ChatColors.Default} Конфиг перезагружен.");
        else Console.WriteLine("[VoteSystem] Config reloaded.");
    }

    // =================== ЛОГИКА ===================

    private string? PreCheckVote(CCSPlayerController caller)
    {
        if (_activeVote != null)
            return $" {ChatColors.Red}[Vote]{ChatColors.Default} Голосование уже идёт! {ChatColors.Green}!y{ChatColors.Default} / {ChatColors.Red}!n";
        var cd = _config.VoteCooldownSeconds - (DateTime.Now - _lastVoteEnd).TotalSeconds;
        if (cd > 0)
            return $" {ChatColors.Red}[Vote]{ChatColors.Default} Подождите {ChatColors.Yellow}{(int)cd} сек.";
        if (GetOnlinePlayers().Count < _config.MinPlayersToVote)
            return $" {ChatColors.Red}[Vote]{ChatColors.Default} Нужно минимум {ChatColors.Yellow}{_config.MinPlayersToVote}{ChatColors.Default} игроков.";
        return null;
    }

    private void TryStartVote(CCSPlayerController caller, CCSPlayerController target, VoteType type, string reason)
    {
        string? err = PreCheckVote(caller);
        if (err != null) { caller.PrintToChat(err); return; }
        if (target.SteamID == caller.SteamID)
        { caller.PrintToChat($" {ChatColors.Red}[Vote]{ChatColors.Default} Нельзя голосовать против себя."); return; }
        if (_config.ProtectAdmins && AdminManager.PlayerHasPermissions(target, _config.AdminProtectionFlag))
        { caller.PrintToChat($" {ChatColors.Red}[Vote]{ChatColors.Default} Нельзя голосовать против администратора."); return; }
        StartVote(caller, target, type, reason);
    }

    private void HandleVoteCommand(CCSPlayerController caller, CommandInfo command, VoteType type)
    {
        string? err = PreCheckVote(caller);
        if (err != null) { command.ReplyToCommand(err); return; }

        var target = FindTarget(command.GetArg(1), caller);
        if (target == null) { command.ReplyToCommand($" {ChatColors.Red}[Vote]{ChatColors.Default} Игрок не найден."); return; }
        if (target.SteamID == caller.SteamID) { command.ReplyToCommand($" {ChatColors.Red}[Vote]{ChatColors.Default} Нельзя против себя."); return; }
        if (_config.ProtectAdmins && AdminManager.PlayerHasPermissions(target, _config.AdminProtectionFlag))
        { command.ReplyToCommand($" {ChatColors.Red}[Vote]{ChatColors.Default} Нельзя против администратора."); return; }

        string reason = command.ArgCount > 2 ? command.GetArg(2) : "";
        StartVote(caller, target, type, reason);
    }

    private void StartVote(CCSPlayerController initiator, CCSPlayerController target, VoteType type, string reason)
    {
        _activeVote = new VoteData
        {
            InitiatorName = initiator.PlayerName,
            InitiatorSteamId = initiator.SteamID,
            TargetName = target.PlayerName,
            TargetUserId = (int)(target.UserId ?? 0),
            TargetSteamId = target.SteamID,
            TargetSlot = target.Slot,
            Type = type,
            Reason = string.IsNullOrEmpty(reason) ? "Не указана" : reason,
            StartTime = DateTime.Now
        };

        _activeVote.VotesYes.Add(initiator.SteamID);

        string typeStr = GetVoteTypeName(type);
        string color = GetVoteTypeColor(type);

        Server.PrintToChatAll($" ");
        Server.PrintToChatAll($" {color}══════ ГОЛОСОВАНИЕ ══════");
        Server.PrintToChatAll($" {color}[Vote]{ChatColors.Default} {ChatColors.Yellow}{initiator.PlayerName}{ChatColors.Default} начал голосование: {color}{typeStr}");
        Server.PrintToChatAll($" {color}[Vote]{ChatColors.Default} Игрок: {ChatColors.Yellow}{target.PlayerName}");
        if (_activeVote.Reason != "Не указана")
            Server.PrintToChatAll($" {color}[Vote]{ChatColors.Default} Причина: {ChatColors.Grey}{_activeVote.Reason}");
        Server.PrintToChatAll($" {color}[Vote]{ChatColors.Green} !y {ChatColors.Default}— ЗА    {ChatColors.Red}!n {ChatColors.Default}— ПРОТИВ    ({_config.VoteDurationSeconds} сек.)");
        Server.PrintToChatAll($" {color}══════════════════════════");
        Server.PrintToChatAll($" ");

        _activeVote.Timer = AddTimer(_config.VoteDurationSeconds, EndVote);

        if (_config.ShowMidvoteStatus)
        {
            AddTimer(_config.VoteDurationSeconds / 2f, () =>
            {
                if (_activeVote == null) return;
                Server.PrintToChatAll($" {color}[Vote]{ChatColors.Default} Промежуточный итог: {ChatColors.Green}{_activeVote.VotesYes.Count} ЗА{ChatColors.Default} / {ChatColors.Red}{_activeVote.VotesNo.Count} ПРОТИВ{ChatColors.Default}");
            });
        }
    }

    private void CastVote(CCSPlayerController? player, bool voteYes)
    {
        if (player == null || !player.IsValid) return;
        if (_activeVote == null) { player.PrintToChat($" {ChatColors.Red}[Vote]{ChatColors.Default} Нет активного голосования."); return; }

        ulong sid = player.SteamID;
        if (_activeVote.VotesYes.Contains(sid) || _activeVote.VotesNo.Contains(sid))
        { player.PrintToChat($" {ChatColors.Red}[Vote]{ChatColors.Default} Вы уже проголосовали."); return; }

        if (voteYes) _activeVote.VotesYes.Add(sid);
        else _activeVote.VotesNo.Add(sid);

        string v = voteYes ? $"{ChatColors.Green}ЗА" : $"{ChatColors.Red}ПРОТИВ";
        player.PrintToChat($" {ChatColors.Yellow}[Vote]{ChatColors.Default} Вы: {v}{ChatColors.Default} ({ChatColors.Green}{_activeVote.VotesYes.Count}{ChatColors.Default}/{ChatColors.Red}{_activeVote.VotesNo.Count}{ChatColors.Default})");

        if (_config.AllowEarlyFinish && _activeVote.VotesYes.Count + _activeVote.VotesNo.Count >= GetOnlinePlayers().Count)
        {
            _activeVote.Timer?.Kill();
            EndVote();
        }
    }

    private void EndVote()
    {
        if (_activeVote == null) return;
        var vote = _activeVote;
        int yes = vote.VotesYes.Count, no = vote.VotesNo.Count;
        int playerCount = GetOnlinePlayers().Count;
        float percent = playerCount > 0 ? (float)yes / playerCount : 0f;
        bool passed = percent >= _config.VotePassPercent && yes > no;

        string typeStr = GetVoteTypeName(vote.Type);
        string color = GetVoteTypeColor(vote.Type);

        Server.PrintToChatAll($" ");
        if (passed)
        {
            Server.PrintToChatAll($" {ChatColors.Green}══════ ГОЛОСОВАНИЕ ПРИНЯТО ══════");
            Server.PrintToChatAll($" {color}[Vote]{ChatColors.Default} {ChatColors.Green}{yes} ЗА{ChatColors.Default} / {ChatColors.Red}{no} ПРОТИВ{ChatColors.Default} ({(int)(percent * 100)}%)");
            Server.PrintToChatAll($" {color}[Vote]{ChatColors.Default} {color}{typeStr}{ChatColors.Default} игрока {ChatColors.Yellow}{vote.TargetName}{ChatColors.Default} одобрен!");
            Server.PrintToChatAll($" {ChatColors.Green}═════════════════════════════════");
            ExecuteVote(vote);
        }
        else
        {
            Server.PrintToChatAll($" {ChatColors.Red}══════ ГОЛОСОВАНИЕ ОТКЛОНЕНО ══════");
            Server.PrintToChatAll($" {color}[Vote]{ChatColors.Default} {ChatColors.Green}{yes} ЗА{ChatColors.Default} / {ChatColors.Red}{no} ПРОТИВ{ChatColors.Default} ({(int)(percent * 100)}%)");
            Server.PrintToChatAll($" {color}[Vote]{ChatColors.Default} {color}{typeStr}{ChatColors.Default} игрока {ChatColors.Yellow}{vote.TargetName}{ChatColors.Default} отклонён.");
            Server.PrintToChatAll($" {ChatColors.Red}═══════════════════════════════════");
        }
        Server.PrintToChatAll($" ");
        _activeVote = null;
        _lastVoteEnd = DateTime.Now;
    }

    // =================== ИСПОЛНЕНИЕ ===================

    private void ExecuteVote(VoteData vote)
    {
        var target = FindPlayerBySteamId(vote.TargetSteamId);
        if (target == null || !target.IsValid)
        { Server.PrintToChatAll($" {ChatColors.Red}[Vote]{ChatColors.Default} Игрок покинул сервер."); return; }

        switch (vote.Type)
        {
            case VoteType.Kick:
                Server.ExecuteCommand($"kickid {target.UserId} \"Кикнут голосованием: {vote.Reason}\"");
                break;
            case VoteType.Ban:
                ExecuteBan(target, vote);
                break;
            case VoteType.Gag:
                ExecuteGag(target, vote);
                break;
            case VoteType.Mute:
                ExecuteMute(target, vote);
                break;
        }
    }

    private void ExecuteBan(CCSPlayerController target, VoteData vote)
    {
        string reason = $"Voteban: {vote.Reason}";
        string sys = _config.BanSystemType.ToLower();

        // IksAdmin: css_ban #userid — подхватит ник онлайн-игрока
        if (sys == "iks" || sys == "both")
            Server.ExecuteCommand($"css_ban #{target.UserId} {_config.BanDurationMinutes} \"{reason}\"");

        if (sys == "as" || sys == "both")
            Server.ExecuteCommand($"mm_ban #{target.UserId} {_config.BanDurationMinutes * 60} \"{reason}\"");

        AddTimer(1.5f, () =>
        {
            var check = FindPlayerBySteamId(vote.TargetSteamId);
            if (check != null && check.IsValid)
                Server.ExecuteCommand($"kickid {check.UserId} \"Забанен голосованием: {vote.Reason}\"");
        });
    }

    // Gag = ТЕКСТОВЫЙ чат
    private void ExecuteGag(CCSPlayerController target, VoteData vote)
    {
        string reason = $"Votegag: {vote.Reason}";
        string sys = _config.BanSystemType.ToLower();
        int mins = _config.GagDurationSeconds / 60;

        // IksAdmin: css_gag #userid — подхватит ник
        if (sys == "iks" || sys == "both")
            Server.ExecuteCommand($"css_gag #{target.UserId} {mins} \"{reason}\"");

        if (sys == "as" || sys == "both")
            Server.ExecuteCommand($"mm_gag #{target.UserId} {_config.GagDurationSeconds} \"{reason}\"");

        target.PrintToChat($" {ChatColors.Red}[Vote]{ChatColors.Default} Ваш чат заблокирован на {mins} мин.");
    }

    // Mute = ГОЛОСОВОЙ чат
    private void ExecuteMute(CCSPlayerController target, VoteData vote)
    {
        string reason = $"Votemute: {vote.Reason}";
        string sys = _config.BanSystemType.ToLower();
        int mins = _config.MuteDurationSeconds / 60;

        // IksAdmin: css_mute #userid — подхватит ник
        if (sys == "iks" || sys == "both")
            Server.ExecuteCommand($"css_mute #{target.UserId} {mins} \"{reason}\"");

        if (sys == "as" || sys == "both")
            Server.ExecuteCommand($"mm_mute #{target.UserId} {_config.MuteDurationSeconds} \"{reason}\"");

        target.PrintToChat($" {ChatColors.Red}[Vote]{ChatColors.Default} Ваш голосовой чат заблокирован на {mins} мин.");
    }

    private void CancelVote(string reason)
    {
        if (_activeVote == null) return;
        _activeVote.Timer?.Kill();
        Server.PrintToChatAll($" {ChatColors.Red}[Vote]{ChatColors.Default} Голосование отменено: {ChatColors.Yellow}{reason}");
        _activeVote = null;
        _lastVoteEnd = DateTime.Now;
    }

    // =================== УТИЛИТЫ ===================

    private CCSPlayerController? FindTarget(string input, CCSPlayerController caller)
    {
        if (input.StartsWith("#") && int.TryParse(input.Substring(1), out int uid))
            return GetOnlinePlayers().FirstOrDefault(p => p.UserId == uid);

        var matches = GetOnlinePlayers()
            .Where(p => p.PlayerName.Contains(input, StringComparison.OrdinalIgnoreCase)).ToList();

        if (matches.Count == 1) return matches[0];
        if (matches.Count > 1)
        {
            caller.PrintToChat($" {ChatColors.Red}[Vote]{ChatColors.Default} Найдено несколько:");
            foreach (var m in matches.Take(5))
                caller.PrintToChat($"  {ChatColors.Yellow}#{m.UserId}{ChatColors.Default} — {m.PlayerName}");
        }
        return null;
    }

    private CCSPlayerController? FindPlayerBySteamId(ulong steamId)
        => GetOnlinePlayers().FirstOrDefault(p => p.SteamID == steamId);

    private List<CCSPlayerController> GetOnlinePlayers()
        => Utilities.GetPlayers()
            .Where(p => p is { IsValid: true, IsBot: false, IsHLTV: false, Connected: PlayerConnectedState.PlayerConnected })
            .ToList();

    private string GetVoteTypeName(VoteType type) => type switch
    {
        VoteType.Kick => "КИК",
        VoteType.Ban => "БАН",
        VoteType.Gag => "БЛОК ЧАТА",
        VoteType.Mute => "БЛОК ВОЙСА",
        _ => "?"
    };

    private string GetVoteTypeColor(VoteType type) => type switch
    {
        VoteType.Kick => $"{ChatColors.Yellow}",
        VoteType.Ban => $"{ChatColors.Red}",
        VoteType.Gag => $"{ChatColors.Purple}",
        VoteType.Mute => $"{ChatColors.Blue}",
        _ => $"{ChatColors.Default}"
    };
}
