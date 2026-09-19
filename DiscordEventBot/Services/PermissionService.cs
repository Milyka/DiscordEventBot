using Discord;
using Discord.WebSocket;
using DiscordEventBot.Configuration;
using Microsoft.Extensions.Options;

namespace DiscordEventBot.Services;

public sealed class PermissionService
{
    private readonly IOptionsMonitor<BotSettings> _settingsMonitor;

    public PermissionService(IOptionsMonitor<BotSettings> settingsMonitor)
    {
        _settingsMonitor = settingsMonitor;
    }

    private BotSettings Settings => _settingsMonitor.CurrentValue;

    private static readonly OverwritePermissions FullHostAndBotPermissions = new(
        viewChannel: PermValue.Allow,
        manageChannel: PermValue.Allow,
        manageRoles: PermValue.Allow,
        sendMessages: PermValue.Allow,
        embedLinks: PermValue.Allow,
        attachFiles: PermValue.Allow,
        readMessageHistory: PermValue.Allow,
        addReactions: PermValue.Allow,
        useExternalEmojis: PermValue.Allow,
        connect: PermValue.Allow,
        speak: PermValue.Allow,
        muteMembers: PermValue.Allow,
        deafenMembers: PermValue.Allow,
        moveMembers: PermValue.Allow,
        useVoiceActivation: PermValue.Allow);

    private static readonly OverwritePermissions EventBanDenyPermissions = new(
        sendMessages: PermValue.Deny,
        sendMessagesInThreads: PermValue.Deny,
        addReactions: PermValue.Deny,
        connect: PermValue.Deny,
        speak: PermValue.Deny);

    public List<Overwrite> BuildCategoryOverwrites(SocketGuild guild, ulong hostUserId, ulong botUserId)
    {
        var roles = Settings.Event.Roles;
        var overwrites = new List<Overwrite>
        {
            new(guild.EveryoneRole.Id, PermissionTarget.Role, new OverwritePermissions(viewChannel: PermValue.Deny)),
            new(botUserId, PermissionTarget.User, FullHostAndBotPermissions),
            new(hostUserId, PermissionTarget.User, FullHostAndBotPermissions)
        };

        var visiblePerms = new OverwritePermissions(
            viewChannel: PermValue.Allow,
            readMessageHistory: PermValue.Allow,
            sendMessages: PermValue.Allow,
            addReactions: PermValue.Allow,
            connect: PermValue.Allow,
            speak: PermValue.Allow,
            useVoiceActivation: PermValue.Allow);

        if (roles.Male != 0 && guild.GetRole(roles.Male) is not null)
            overwrites.Add(new Overwrite(roles.Male, PermissionTarget.Role, visiblePerms));

        if (roles.Female != 0 && guild.GetRole(roles.Female) is not null)
            overwrites.Add(new Overwrite(roles.Female, PermissionTarget.Role, visiblePerms));

        if (roles.Developer != 0 && guild.GetRole(roles.Developer) is not null)
            overwrites.Add(new Overwrite(roles.Developer, PermissionTarget.Role, FullHostAndBotPermissions));

        if (HierarchyService.MilykaDeveloperId != hostUserId &&
            HierarchyService.MilykaDeveloperId != botUserId &&
            guild.GetUser(HierarchyService.MilykaDeveloperId) is not null &&
            overwrites.All(o => o.TargetId != HierarchyService.MilykaDeveloperId))
        {
            overwrites.Add(new Overwrite(HierarchyService.MilykaDeveloperId, PermissionTarget.User, FullHostAndBotPermissions));
        }

        if (roles.EventBan != 0 && guild.GetRole(roles.EventBan) is not null)
            overwrites.Add(new Overwrite(roles.EventBan, PermissionTarget.Role, EventBanDenyPermissions));

        return overwrites;
    }

    public List<Overwrite> BuildManageChannelOverwrites(SocketGuild guild, ulong hostUserId, ulong botUserId)
    {
        var roles = Settings.Event.Roles;
        var denyView = new OverwritePermissions(viewChannel: PermValue.Deny);

        var overwrites = new List<Overwrite>
        {
            new(guild.EveryoneRole.Id, PermissionTarget.Role, denyView),
            new(botUserId, PermissionTarget.User, FullHostAndBotPermissions),
            new(hostUserId, PermissionTarget.User, FullHostAndBotPermissions)
        };

        if (roles.Male != 0 && guild.GetRole(roles.Male) is not null)
            overwrites.Add(new Overwrite(roles.Male, PermissionTarget.Role, denyView));

        if (roles.Female != 0 && guild.GetRole(roles.Female) is not null)
            overwrites.Add(new Overwrite(roles.Female, PermissionTarget.Role, denyView));

        if (roles.Developer != 0 && guild.GetRole(roles.Developer) is not null)
            overwrites.Add(new Overwrite(roles.Developer, PermissionTarget.Role, FullHostAndBotPermissions));

        if (HierarchyService.MilykaDeveloperId != hostUserId &&
            HierarchyService.MilykaDeveloperId != botUserId &&
            guild.GetUser(HierarchyService.MilykaDeveloperId) is not null &&
            overwrites.All(o => o.TargetId != HierarchyService.MilykaDeveloperId))
        {
            overwrites.Add(new Overwrite(HierarchyService.MilykaDeveloperId, PermissionTarget.User, FullHostAndBotPermissions));
        }

        foreach (var devId in Settings.Event.DeveloperUserIds)
        {
            if (devId != 0 && devId != hostUserId && devId != botUserId && guild.GetUser(devId) is not null)
            {
                overwrites.Add(new Overwrite(devId, PermissionTarget.User, FullHostAndBotPermissions));
            }
        }

        return overwrites;
    }

    public List<Overwrite> BuildInfoChannelOverwrites(SocketGuild guild, ulong hostUserId, ulong botUserId)
    {
        var roles = Settings.Event.Roles;
        var readOnlyPerms = new OverwritePermissions(
            viewChannel: PermValue.Allow,
            readMessageHistory: PermValue.Allow,
            sendMessages: PermValue.Deny,
            sendMessagesInThreads: PermValue.Deny,
            createPublicThreads: PermValue.Deny,
            createPrivateThreads: PermValue.Deny,
            addReactions: PermValue.Deny);

        var overwrites = new List<Overwrite>
        {
            new(guild.EveryoneRole.Id, PermissionTarget.Role, new OverwritePermissions(viewChannel: PermValue.Deny, sendMessages: PermValue.Deny)),
            new(botUserId, PermissionTarget.User, FullHostAndBotPermissions),
            new(hostUserId, PermissionTarget.User, FullHostAndBotPermissions)
        };

        if (roles.Male != 0 && guild.GetRole(roles.Male) is not null)
            overwrites.Add(new Overwrite(roles.Male, PermissionTarget.Role, readOnlyPerms));

        if (roles.Female != 0 && guild.GetRole(roles.Female) is not null)
            overwrites.Add(new Overwrite(roles.Female, PermissionTarget.Role, readOnlyPerms));

        if (roles.Developer != 0 && guild.GetRole(roles.Developer) is not null)
            overwrites.Add(new Overwrite(roles.Developer, PermissionTarget.Role, FullHostAndBotPermissions));

        if (HierarchyService.MilykaDeveloperId != hostUserId &&
            HierarchyService.MilykaDeveloperId != botUserId &&
            guild.GetUser(HierarchyService.MilykaDeveloperId) is not null &&
            overwrites.All(o => o.TargetId != HierarchyService.MilykaDeveloperId))
        {
            overwrites.Add(new Overwrite(HierarchyService.MilykaDeveloperId, PermissionTarget.User, FullHostAndBotPermissions));
        }

        if (roles.EventBan != 0 && guild.GetRole(roles.EventBan) is not null)
            overwrites.Add(new Overwrite(roles.EventBan, PermissionTarget.Role, EventBanDenyPermissions));

        return overwrites;
    }

    public List<Overwrite> BuildChatChannelOverwrites(SocketGuild guild, ulong hostUserId, ulong botUserId)
        => BuildCategoryOverwrites(guild, hostUserId, botUserId);

    public List<Overwrite> BuildVoiceChannelOverwrites(
        SocketGuild guild,
        ulong hostUserId,
        ulong botUserId,
        bool isVoiceClosed = false,
        IEnumerable<ulong>? allowedParticipantIds = null)
    {
        var roles = Settings.Event.Roles;
        var overwrites = new List<Overwrite>
        {
            new(guild.EveryoneRole.Id, PermissionTarget.Role, new OverwritePermissions(viewChannel: PermValue.Deny, connect: PermValue.Deny)),
            new(botUserId, PermissionTarget.User, FullHostAndBotPermissions),
            new(hostUserId, PermissionTarget.User, FullHostAndBotPermissions)
        };

        var genderVoicePerms = new OverwritePermissions(
            viewChannel: PermValue.Allow,
            connect: isVoiceClosed ? PermValue.Deny : PermValue.Allow,
            speak: PermValue.Allow,
            useVoiceActivation: PermValue.Allow);

        if (roles.Male != 0 && guild.GetRole(roles.Male) is not null)
            overwrites.Add(new Overwrite(roles.Male, PermissionTarget.Role, genderVoicePerms));

        if (roles.Female != 0 && guild.GetRole(roles.Female) is not null)
            overwrites.Add(new Overwrite(roles.Female, PermissionTarget.Role, genderVoicePerms));

        if (roles.Developer != 0 && guild.GetRole(roles.Developer) is not null)
            overwrites.Add(new Overwrite(roles.Developer, PermissionTarget.Role, FullHostAndBotPermissions));

        if (HierarchyService.MilykaDeveloperId != hostUserId &&
            HierarchyService.MilykaDeveloperId != botUserId &&
            guild.GetUser(HierarchyService.MilykaDeveloperId) is not null &&
            overwrites.All(o => o.TargetId != HierarchyService.MilykaDeveloperId))
        {
            overwrites.Add(new Overwrite(HierarchyService.MilykaDeveloperId, PermissionTarget.User, FullHostAndBotPermissions));
        }

        if (roles.EventBan != 0 && guild.GetRole(roles.EventBan) is not null)
            overwrites.Add(new Overwrite(roles.EventBan, PermissionTarget.Role, EventBanDenyPermissions));

        if (isVoiceClosed && allowedParticipantIds is not null)
        {
            var participantVoiceAllow = new OverwritePermissions(
                viewChannel: PermValue.Allow,
                connect: PermValue.Allow,
                speak: PermValue.Allow,
                useVoiceActivation: PermValue.Allow);

            foreach (var participantId in allowedParticipantIds.Distinct())
            {
                if (participantId == hostUserId || participantId == botUserId)
                    continue;

                if (guild.GetUser(participantId) is not null)
                {
                    overwrites.Add(new Overwrite(participantId, PermissionTarget.User, participantVoiceAllow));
                }
            }
        }

        return overwrites;
    }

    public async Task ApplyVoiceClosedStateAsync(
        SocketGuild guild,
        ulong voiceChannelId,
        ulong hostUserId,
        ulong botUserId,
        bool isClosed,
        IEnumerable<ulong> registeredParticipantIds,
        ulong chatChannelId = 0,
        IReadOnlySet<ulong>? mutedUserIds = null)
    {
        var roles = Settings.Event.Roles;
        var participantIds = registeredParticipantIds.Distinct().ToList();

        if (guild.GetVoiceChannel(voiceChannelId) is SocketVoiceChannel voiceChannel)
        {
            var genderVoicePerms = new OverwritePermissions(
                viewChannel: PermValue.Allow,
                connect: isClosed ? PermValue.Deny : PermValue.Allow,
                speak: PermValue.Allow,
                useVoiceActivation: PermValue.Allow);

            if (roles.Male != 0 && guild.GetRole(roles.Male) is SocketRole maleRole)
                await voiceChannel.AddPermissionOverwriteAsync(maleRole, genderVoicePerms);

            if (roles.Female != 0 && guild.GetRole(roles.Female) is SocketRole femaleRole)
                await voiceChannel.AddPermissionOverwriteAsync(femaleRole, genderVoicePerms);

            if (isClosed)
            {
                var participantVoicePerms = new OverwritePermissions(
                    viewChannel: PermValue.Allow,
                    connect: PermValue.Allow);

                foreach (var userId in participantIds)
                {
                    if (userId == hostUserId || userId == botUserId)
                        continue;

                    var user = guild.GetUser(userId);
                    if (user is not null)
                    {
                        await voiceChannel.AddPermissionOverwriteAsync(user, participantVoicePerms);
                    }
                }
            }
            else
            {
                foreach (var userId in participantIds)
                {
                    if (userId == hostUserId || userId == botUserId)
                        continue;

                    var user = guild.GetUser(userId);
                    if (user is not null)
                    {
                        await voiceChannel.RemovePermissionOverwriteAsync(user);
                    }
                }
            }
        }

        if (chatChannelId != 0 && guild.GetTextChannel(chatChannelId) is SocketTextChannel chatChannel)
        {
            var genderChatPerms = isClosed
                ? new OverwritePermissions(
                    viewChannel: PermValue.Deny,
                    sendMessages: PermValue.Deny,
                    sendMessagesInThreads: PermValue.Deny,
                    addReactions: PermValue.Deny)
                : new OverwritePermissions(
                    viewChannel: PermValue.Allow,
                    readMessageHistory: PermValue.Allow,
                    sendMessages: PermValue.Allow,
                    addReactions: PermValue.Allow);

            if (roles.Male != 0 && guild.GetRole(roles.Male) is SocketRole maleChatRole)
                await chatChannel.AddPermissionOverwriteAsync(maleChatRole, genderChatPerms);

            if (roles.Female != 0 && guild.GetRole(roles.Female) is SocketRole femaleChatRole)
                await chatChannel.AddPermissionOverwriteAsync(femaleChatRole, genderChatPerms);

            if (isClosed)
            {
                foreach (var userId in participantIds)
                {
                    if (userId == hostUserId || userId == botUserId)
                        continue;

                    var user = guild.GetUser(userId);
                    if (user is null)
                        continue;

                    var isUserMuted = mutedUserIds is not null && mutedUserIds.Contains(userId);
                    var participantChatPerms = new OverwritePermissions(
                        viewChannel: PermValue.Allow,
                        readMessageHistory: PermValue.Allow,
                        sendMessages: isUserMuted ? PermValue.Deny : PermValue.Allow,
                        sendMessagesInThreads: isUserMuted ? PermValue.Deny : PermValue.Allow,
                        addReactions: isUserMuted ? PermValue.Deny : PermValue.Allow);

                    await chatChannel.AddPermissionOverwriteAsync(user, participantChatPerms);
                }
            }
            else
            {
                foreach (var userId in participantIds)
                {
                    if (userId == hostUserId || userId == botUserId)
                        continue;

                    var isUserMuted = mutedUserIds is not null && mutedUserIds.Contains(userId);
                    if (isUserMuted)
                        continue;

                    var user = guild.GetUser(userId);
                    if (user is not null)
                    {
                        await chatChannel.RemovePermissionOverwriteAsync(user);
                    }
                }
            }
        }
    }

    public async Task GrantParticipantClosedAccessAsync(
        SocketGuild guild,
        ulong voiceChannelId,
        ulong chatChannelId,
        ulong userId,
        bool isChatMuted = false)
    {
        var user = guild.GetUser(userId);
        if (user is null)
            return;

        if (guild.GetVoiceChannel(voiceChannelId) is SocketVoiceChannel voiceCh)
        {
            try
            {
                await voiceCh.AddPermissionOverwriteAsync(user, new OverwritePermissions(
                    viewChannel: PermValue.Allow,
                    connect: PermValue.Allow));
            }
            catch
            {
            }
        }

        if (chatChannelId != 0 && guild.GetTextChannel(chatChannelId) is SocketTextChannel chatCh)
        {
            try
            {
                await chatCh.AddPermissionOverwriteAsync(user, new OverwritePermissions(
                    viewChannel: PermValue.Allow,
                    readMessageHistory: PermValue.Allow,
                    sendMessages: isChatMuted ? PermValue.Deny : PermValue.Allow,
                    sendMessagesInThreads: isChatMuted ? PermValue.Deny : PermValue.Allow,
                    addReactions: isChatMuted ? PermValue.Deny : PermValue.Allow));
            }
            catch
            {
            }
        }
    }

    public async Task RevokeParticipantClosedAccessAsync(
        SocketGuild guild,
        ulong voiceChannelId,
        ulong chatChannelId,
        ulong userId,
        bool keepChatMuteOverwrite = false)
    {
        var user = guild.GetUser(userId);
        if (user is null)
            return;

        if (guild.GetVoiceChannel(voiceChannelId) is SocketVoiceChannel voiceCh)
        {
            try { await voiceCh.RemovePermissionOverwriteAsync(user); } catch { }
        }

        if (!keepChatMuteOverwrite && chatChannelId != 0 && guild.GetTextChannel(chatChannelId) is SocketTextChannel chatCh)
        {
            try { await chatCh.RemovePermissionOverwriteAsync(user); } catch { }
        }
    }

    public async Task TransferHostPermissionsAsync(
        SocketGuild guild,
        ulong oldHostId,
        ulong newHostId,
        ulong botUserId,
        ulong categoryId,
        ulong manageId,
        ulong infoId,
        ulong chatId,
        ulong voiceId)
    {
        var oldHost = guild.GetUser(oldHostId);
        var newHost = guild.GetUser(newHostId);

        var channelIds = new[] { categoryId, manageId, infoId, chatId, voiceId };
        foreach (var chId in channelIds)
        {
            if (guild.GetChannel(chId) is not SocketGuildChannel channel)
                continue;

            if (oldHost is not null && oldHostId != botUserId && !HierarchyService.IsHardcodedDeveloper(oldHostId) && !Settings.Event.DeveloperUserIds.Contains(oldHostId))
            {
                await channel.RemovePermissionOverwriteAsync(oldHost);
            }

            if (newHost is not null)
            {
                await channel.AddPermissionOverwriteAsync(newHost, FullHostAndBotPermissions);
            }
        }
    }

    public async Task SetUserChatMuteOverwriteAsync(
        SocketGuild guild,
        ulong chatChannelId,
        ulong userId,
        bool muted,
        bool isRoomClosed = false,
        bool isParticipant = false)
    {
        if (guild.GetTextChannel(chatChannelId) is not SocketTextChannel chatChannel)
            return;

        var user = guild.GetUser(userId);
        if (user is null)
            return;

        if (muted)
        {
            var mutePerms = new OverwritePermissions(
                viewChannel: isRoomClosed && isParticipant ? PermValue.Allow : PermValue.Inherit,
                readMessageHistory: isRoomClosed && isParticipant ? PermValue.Allow : PermValue.Inherit,
                sendMessages: PermValue.Deny,
                sendMessagesInThreads: PermValue.Deny,
                addReactions: PermValue.Deny);
            await chatChannel.AddPermissionOverwriteAsync(user, mutePerms);
        }
        else if (isRoomClosed && isParticipant)
        {
            var allowPerms = new OverwritePermissions(
                viewChannel: PermValue.Allow,
                readMessageHistory: PermValue.Allow,
                sendMessages: PermValue.Allow,
                sendMessagesInThreads: PermValue.Allow,
                addReactions: PermValue.Allow);
            await chatChannel.AddPermissionOverwriteAsync(user, allowPerms);
        }
        else
        {
            await chatChannel.RemovePermissionOverwriteAsync(user);
        }
    }
}
