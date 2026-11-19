using HhAutoApplier.Application.Dtos;
using HhAutoApplier.Application.Errors;
using OneOf;

namespace HhAutoApplier.Application.Queries.Dashboard;

// Система на дашборде (#121): только администратор. Period — 7, 30 или 90 дней для разреза по пользователям
public record GetSystemDashboardQuery(int Period = 30);

// На .NET 11: public union GetSystemDashboardResult(SystemDashboardDto, ValidationError, ForbiddenError);
[GenerateOneOf]
public partial class GetSystemDashboardResult : OneOfBase<SystemDashboardDto, ValidationError, ForbiddenError>;
