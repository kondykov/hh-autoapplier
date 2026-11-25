using HhAutoApplier.Domain.Dialogs;

namespace HhAutoApplier.Application.Dtos;

public record EmployerLinkDto(long ChatId, string Title, string Company, string Key, string Url, string Domain,
    EmployerLinkKind Kind, EmployerLinkState State, string Quote, string? Action, DateTimeOffset DetectedAt,
    DateTimeOffset? DueAt, DateTimeOffset? RemindAt, DateTimeOffset? RemindedAt)
{
    public static EmployerLinkDto From(EmployerMessage message, EmployerLink link) =>
        new(message.ChatId, message.Title, message.Company, link.Key, link.Url, link.Domain, link.Kind, link.State,
            link.Quote, link.Action, link.DetectedAt, link.DueAt, link.RemindAt, link.RemindedAt);
}
