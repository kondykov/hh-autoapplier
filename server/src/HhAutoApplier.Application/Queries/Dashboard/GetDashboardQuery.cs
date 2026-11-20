using HhAutoApplier.Application.Dtos;
using HhAutoApplier.Application.Errors;
using OneOf;

namespace HhAutoApplier.Application.Queries.Dashboard;

// Дашборд профиля (#121): свои данные; администратор — пользователя UserId. Period — 7, 30 или 90 дней,
// Source — агрегатор ("hh"), "all" — все. Тот же запрос потом отдаст данные боту (#106)
public record GetDashboardQuery(int Period = 30, Guid? UserId = null, string Source = DashboardMath.AllSources);

// На .NET 11: public union GetDashboardResult(DashboardDto, ValidationError, ForbiddenError);
[GenerateOneOf]
public partial class GetDashboardResult : OneOfBase<DashboardDto, ValidationError, ForbiddenError>;
