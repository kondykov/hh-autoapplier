using HhAutoApplier.Application.Dtos;
using HhAutoApplier.Application.Errors;
using OneOf;

namespace HhAutoApplier.Application.Queries.Dialogs;

// Своё сообщение работодателя по чату hh.ru — карточка в Telegram-боте (#115)
public record GetDialogQuery(long ChatId);

// На .NET 11: public union GetDialogResult(EmployerMessageDto, NotFoundError);
[GenerateOneOf]
public partial class GetDialogResult : OneOfBase<EmployerMessageDto, NotFoundError>;
