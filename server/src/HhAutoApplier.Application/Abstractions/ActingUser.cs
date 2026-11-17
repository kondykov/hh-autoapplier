using HhAutoApplier.Application.Dtos;

namespace HhAutoApplier.Application.Abstractions;

// Пользователь вне HTTP-запроса — владелец привязанного чата Telegram (#115). Пока он задан, ICurrentUser отдаёт его:
// бот вызывает те же обработчики сценариев, что и контроллеры, с теми же проверками прав. Живёт в области одного обновления бота
public sealed class ActingUser
{
    public UserDto? User { get; private set; }

    public void Act(UserDto user) => User = user;
}
