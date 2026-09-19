using Microsoft.AspNetCore.Mvc;
using PTGOilSystem.Web.Security.Mobile;
using PTGOilSystem.Web.Services.Mobile;
using PTGOilSystem.Web.Services.Reporting;

namespace PTGOilSystem.Web.Infrastructure.Api;

public static class MobileApiServiceCollectionExtensions
{
    public static IServiceCollection AddPtgMobileApi(this IServiceCollection services)
    {
        services.AddScoped<IMobileTokenService, MobileTokenService>();
        services.AddScoped<IMobileUserProfileService, MobileUserProfileService>();
        services.AddScoped<IMobileDashboardService, MobileDashboardService>();
        services.AddScoped<IGoodsInTransitReader, GoodsInTransitReader>();
        services.AddScoped<ICashPositionReader, CashPositionReader>();

        // PostConfigure: پس از ApiBehaviorOptionsSetupِ خودِ MVC اجرا شود. فقط کنترلرهای [ApiController] را تحت تأثیر قرار می‌دهد؛ کنترلرهای وب این ویژگی را ندارند.
        services.PostConfigure<ApiBehaviorOptions>(options =>
        {
            options.InvalidModelStateResponseFactory = context =>
            {
                var problem = new ValidationProblemDetails(context.ModelState)
                {
                    Status = StatusCodes.Status400BadRequest,
                    Title = "Bad Request",
                    Detail = ApiProblem.ValidationMessage,
                    Type = "about:blank"
                };
                ApiProblem.Decorate(problem, context.HttpContext, ApiErrorCodes.Validation);

                var result = new BadRequestObjectResult(problem);
                result.ContentTypes.Add(ApiProblem.ContentType);
                return result;
            };
        });

        return services;
    }
}
