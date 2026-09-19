using Discord;
using Discord.WebSocket;
using DiscordEventBot.Configuration;
using DiscordEventBot.Data;
using Microsoft.Extensions.Options;

namespace DiscordEventBot.Services;

public enum StaffRank
{
    None = 0,
    EventMod = 1,
    Master = 2,
    Curator = 3,
    Admingroup = 4,
    Mentor = 5,
    StaffAdmin = 6,
    Developer = 7
}

public sealed class HierarchyService
{
    public const ulong MilykaDeveloperId = 1059057265593823292UL;
    public const ulong HardcodedDeveloperRoleId = 1463204361583722527UL;

    private readonly IOptionsMonitor<BotSettings> _settingsMonitor;

    public HierarchyService(IOptionsMonitor<BotSettings> settingsMonitor)
    {
        _settingsMonitor = settingsMonitor;
    }

    public BotSettings Settings => _settingsMonitor.CurrentValue;

    public static bool IsHardcodedDeveloper(ulong userId)
        => userId == MilykaDeveloperId || userId == BotSettings.HardcodedDeveloperUserId;

    public StaffRank GetRank(IGuildUser? user)
    {
        if (user is null)
            return StaffRank.None;

        if (IsHardcodedDeveloper(user.Id))
            return StaffRank.Developer;

        if (user is SocketGuildUser socketUser)
            return GetRankFromSocketUser(socketUser);

        return GetRank(user.Id, user.RoleIds);
    }

    public StaffRank GetRank(SocketGuild guild, ulong userId)
    {
        if (IsHardcodedDeveloper(userId))
            return StaffRank.Developer;

        var settings = Settings.Event;
        if (settings.DeveloperUserIds.Contains(userId))
            return StaffRank.Developer;

        var guildUser = guild.GetUser(userId);
        if (guildUser is null)
            return StaffRank.None;

        return GetRankFromSocketUser(guildUser);
    }

    private StaffRank GetRankFromSocketUser(SocketGuildUser guildUser)
    {
        if (IsHardcodedDeveloper(guildUser.Id))
            return StaffRank.Developer;

        var settings = Settings.Event;
        if (settings.DeveloperUserIds.Contains(guildUser.Id))
            return StaffRank.Developer;

        var rolesConfig = settings.Roles;
        var roleIds = guildUser.Roles.Select(r => r.Id).ToHashSet();

        if (roleIds.Contains(HardcodedDeveloperRoleId) || (rolesConfig.Developer != 0 && roleIds.Contains(rolesConfig.Developer)))
            return StaffRank.Developer;

        var distinctStaffRoleIds = new[]
        {
            rolesConfig.StaffAdmin,
            rolesConfig.Mentor,
            rolesConfig.Admingroup,
            rolesConfig.Curator,
            rolesConfig.Master,
            rolesConfig.EventMod
        }.Where(id => id != 0).Distinct().Count();

        if (distinctStaffRoleIds == 1)
        {
            foreach (var role in guildUser.Roles.OrderByDescending(r => r.Position))
            {
                var name = role.Name.ToLowerInvariant();
                if (name.Contains("developer"))
                    return StaffRank.Developer;
                if (name.Contains("administrator") || name.Contains("staffadmin"))
                    return StaffRank.StaffAdmin;
                if (name.Contains("mentor"))
                    return StaffRank.Mentor;
                if (name.Contains("admingroup"))
                    return StaffRank.Admingroup;
                if (name.Contains("curator"))
                    return StaffRank.Curator;
                if (name.Contains("master"))
                    return StaffRank.Master;
                if (name.Contains("eventsmod") || name.Contains("eventmod") || name.Contains("event mod"))
                    return StaffRank.EventMod;
            }
        }

        return GetRank(guildUser.Id, roleIds);
    }

    public StaffRank GetRank(ulong userId, IEnumerable<ulong> roleIds)
    {
        if (IsHardcodedDeveloper(userId))
            return StaffRank.Developer;

        var settings = Settings.Event;
        var rolesConfig = settings.Roles;

        if (settings.DeveloperUserIds.Contains(userId))
            return StaffRank.Developer;

        var roleSet = roleIds as IReadOnlySet<ulong> ?? roleIds.ToHashSet();

        if (roleSet.Contains(HardcodedDeveloperRoleId) || (rolesConfig.Developer != 0 && roleSet.Contains(rolesConfig.Developer)))
            return StaffRank.Developer;

        if (rolesConfig.StaffAdmin != 0 && roleSet.Contains(rolesConfig.StaffAdmin))
            return StaffRank.StaffAdmin;

        if (rolesConfig.Mentor != 0 && roleSet.Contains(rolesConfig.Mentor))
            return StaffRank.Mentor;

        if (rolesConfig.Admingroup != 0 && roleSet.Contains(rolesConfig.Admingroup))
            return StaffRank.Admingroup;

        var hasEventModRole = rolesConfig.EventMod != 0 && roleSet.Contains(rolesConfig.EventMod);

        if (hasEventModRole && rolesConfig.Curator != 0 && roleSet.Contains(rolesConfig.Curator))
            return StaffRank.Curator;

        if (hasEventModRole && rolesConfig.Master != 0 && roleSet.Contains(rolesConfig.Master))
            return StaffRank.Master;

        if (rolesConfig.Curator != 0 && roleSet.Contains(rolesConfig.Curator))
            return StaffRank.Curator;

        if (rolesConfig.Master != 0 && roleSet.Contains(rolesConfig.Master))
            return StaffRank.Master;

        if (hasEventModRole)
            return StaffRank.EventMod;

        return StaffRank.None;
    }

    public bool IsDeveloper(ulong userId)
        => IsHardcodedDeveloper(userId) || Settings.Event.DeveloperUserIds.Contains(userId);

    public bool IsDeveloper(IGuildUser? user)
        => user is not null && (IsHardcodedDeveloper(user.Id) || GetRank(user) == StaffRank.Developer);

    public bool IsDeveloper(SocketGuild guild, ulong userId)
        => IsHardcodedDeveloper(userId) || GetRank(guild, userId) == StaffRank.Developer;

    public bool HasEventAccess(IGuildUser? user)
        => user is not null && (IsHardcodedDeveloper(user.Id) || GetRank(user) >= StaffRank.EventMod);

    public bool HasGenderAccess(IGuildUser? user)
    {
        if (user is null)
            return false;

        if (IsHardcodedDeveloper(user.Id) || IsDeveloper(user))
            return true;

        var roles = Settings.Event.Roles;
        if (roles.Male == 0 && roles.Female == 0)
            return true;

        return (roles.Male != 0 && user.RoleIds.Contains(roles.Male))
            || (roles.Female != 0 && user.RoleIds.Contains(roles.Female));
    }

    public bool HasEventBanRole(IGuildUser? user)
    {
        if (user is null || IsHardcodedDeveloper(user.Id) || IsDeveloper(user))
            return false;

        var banRoleId = Settings.Event.Roles.EventBan;
        return banRoleId != 0 && user.RoleIds.Contains(banRoleId);
    }

    public bool CanControl(IGuildUser? actor, IGuildUser? target)
    {
        if (actor is null)
            return false;

        if (IsHardcodedDeveloper(actor.Id))
            return true;

        if (target is not null && (IsHardcodedDeveloper(target.Id) || IsDeveloper(target)))
            return false;

        var actorRank = GetRank(actor);
        if (actorRank == StaffRank.Developer)
            return true;

        if (actorRank < StaffRank.EventMod)
            return false;

        var targetRank = GetRank(target);
        if (actorRank > targetRank)
            return true;

        if (actorRank == targetRank && actor is SocketGuildUser sActor && target is SocketGuildUser sTarget)
            return sActor.Hierarchy > sTarget.Hierarchy;

        return false;
    }

    public bool CanControl(SocketGuild guild, ulong actorUserId, ulong targetUserId)
    {
        if (IsHardcodedDeveloper(actorUserId))
            return true;

        if (IsHardcodedDeveloper(targetUserId))
            return false;

        if (IsDeveloper(guild, targetUserId) && actorUserId != targetUserId)
        {
            var actorIsDev = IsDeveloper(guild, actorUserId);
            if (!actorIsDev)
                return false;
        }

        var actorRank = GetRank(guild, actorUserId);
        if (actorRank == StaffRank.Developer)
            return true;

        if (actorRank < StaffRank.EventMod)
            return false;

        var targetRank = GetRank(guild, targetUserId);
        if (actorRank > targetRank)
            return true;

        if (actorRank == targetRank)
        {
            var sActor = guild.GetUser(actorUserId);
            var sTarget = guild.GetUser(targetUserId);
            if (sActor is not null && sTarget is not null && sActor.Hierarchy > sTarget.Hierarchy)
                return true;
        }

        return false;
    }

    public bool CanUseManagementChannel(IGuildUser? actor, EventEntity ev)
    {
        if (actor is null)
            return false;

        return IsHardcodedDeveloper(actor.Id) || actor.Id == ev.HostUserId || IsDeveloper(actor);
    }

    public bool CanManageOrControlEvent(SocketGuild guild, IGuildUser? actor, EventEntity ev)
    {
        if (actor is null)
            return false;

        if (IsHardcodedDeveloper(actor.Id) || actor.Id == ev.HostUserId || IsDeveloper(actor))
            return true;

        return CanControl(guild, actor.Id, ev.HostUserId);
    }

    public bool CanPunish(
        SocketGuild guild,
        IGuildUser? actor,
        ulong targetUserId,
        ulong eventHostId,
        ulong botUserId,
        out string errorMessage)
    {
        errorMessage = string.Empty;

        if (actor is null)
        {
            errorMessage = "Не удалось определить пользователя.";
            return false;
        }

        if (targetUserId == botUserId)
        {
            errorMessage = "Нельзя применять наказания к боту.";
            return false;
        }

        var targetGuildUser = guild.GetUser(targetUserId);
        if (targetGuildUser is not null && targetGuildUser.IsBot)
        {
            errorMessage = "Нельзя применять наказания к боту.";
            return false;
        }

        if (IsHardcodedDeveloper(targetUserId) || IsDeveloper(guild, targetUserId))
        {
            errorMessage = "Нельзя применять наказания (бан, мут, кик) к пользователю со статусом Developer.";
            return false;
        }

        if (targetUserId == actor.Id)
        {
            errorMessage = "Нельзя применять наказания к самому себе.";
            return false;
        }

        if (IsHardcodedDeveloper(actor.Id) || IsDeveloper(actor))
            return true;

        if (targetUserId == eventHostId)
        {
            errorMessage = "Нельзя применять наказания к ведущему ивента.";
            return false;
        }

        if (actor.Id == eventHostId)
        {
            var actorRank = GetRank(guild, actor.Id);
            var targetRank = GetRank(guild, targetUserId);
            if (actorRank >= targetRank)
                return true;
        }

        if (!CanControl(guild, actor.Id, targetUserId))
        {
            errorMessage = "Нельзя применять наказания к пользователю, чей ранг не ниже вашего.";
            return false;
        }

        return true;
    }

    public static string FormatRankName(StaffRank rank) => rank switch
    {
        StaffRank.Developer => "Developer",
        StaffRank.StaffAdmin => "Staff admin",
        StaffRank.Mentor => "Mentor",
        StaffRank.Admingroup => "Admingroup",
        StaffRank.Curator => "Curator",
        StaffRank.Master => "Master",
        StaffRank.EventMod => "Event mod",
        _ => "Участник"
    };
}
