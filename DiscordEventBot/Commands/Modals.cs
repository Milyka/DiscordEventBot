using Discord;
using Discord.Interactions;

namespace DiscordEventBot.Commands;

public sealed class CustomEventModal : IModal
{
    public string Title => "Создание кастомного ивента";

    [InputLabel("Название ивента")]
    [ModalTextInput("name", TextInputStyle.Short, "Введите название (1–100 символов)", minLength: 1, maxLength: 100)]
    public string Name { get; set; } = string.Empty;

    [RequiredInput(false)]
    [InputLabel("Описание / правила (необязательно)")]
    [ModalTextInput("description", TextInputStyle.Paragraph, "Опишите правила или детали ивента (до 500 символов)", maxLength: 500)]
    public string? Description { get; set; }

    [InputLabel("Максимум участников (4–99)")]
    [ModalTextInput("max_participants", TextInputStyle.Short, "По умолчанию: 10", minLength: 1, maxLength: 2, initValue: "10")]
    public string MaxParticipants { get; set; } = "10";
}

public sealed class EditLimitModal : IModal
{
    public string Title => "Изменить лимит участников";

    [InputLabel("Максимум участников (4–99)")]
    [ModalTextInput("max_participants", TextInputStyle.Short, "Число от 4 до 99", minLength: 1, maxLength: 2)]
    public string MaxParticipants { get; set; } = "10";
}

public sealed class EditNameModal : IModal
{
    public string Title => "Изменить название ивента";

    [InputLabel("Новое название (1–100 символов)")]
    [ModalTextInput("name", TextInputStyle.Short, "Введите новое название", minLength: 1, maxLength: 100)]
    public string Name { get; set; } = string.Empty;
}

public sealed class EditDescriptionModal : IModal
{
    public string Title => "Изменить описание ивента";

    [RequiredInput(false)]
    [InputLabel("Описание / правила (до 500 символов)")]
    [ModalTextInput("description", TextInputStyle.Paragraph, "Введите описание или оставьте пустым", maxLength: 500)]
    public string? Description { get; set; }
}

public sealed class RedactEventModal : IModal
{
    public string Title => "Редактирование ивента";

    [InputLabel("Название ивента (1–100 символов)")]
    [ModalTextInput("name", TextInputStyle.Short, "Название ивента", minLength: 1, maxLength: 100)]
    public string Name { get; set; } = string.Empty;

    [RequiredInput(false)]
    [InputLabel("Описание / правила (до 500 символов)")]
    [ModalTextInput("description", TextInputStyle.Paragraph, "Описание / правила", maxLength: 500)]
    public string? Description { get; set; }

    [InputLabel("Максимум участников (4–99)")]
    [ModalTextInput("max_participants", TextInputStyle.Short, "От 4 до 99", minLength: 1, maxLength: 2)]
    public string MaxParticipants { get; set; } = "10";
}

public sealed class TimedMuteModal : IModal
{
    public string Title => "Временный мут участника";

    [InputLabel("Длительность (например: 30m, 2h, 1d)")]
    [ModalTextInput("duration", TextInputStyle.Short, "Минимум 1m (например: 15m, 1h, 1d)", minLength: 1, maxLength: 10, initValue: "30m")]
    public string Duration { get; set; } = "30m";

    [RequiredInput(false)]
    [InputLabel("Причина (необязательно)")]
    [ModalTextInput("reason", TextInputStyle.Short, "Причина мута", maxLength: 200)]
    public string? Reason { get; set; }
}
