using Microsoft.Extensions.DependencyInjection;

namespace Scenarios;

public static partial class Scenarios
{
    public static Task<string> ExceptionHandledByTheMostSpecificHandler() =>
        Risky("invalid");

    public static Task<string> ExceptionHandledThroughABaseExceptionType() =>
        Risky("argument");

    public static Task<string> ExceptionNobodyHandles() =>
        Risky("unsupported");

    public static Task<string> NoExceptionNoHandlers() =>
        Risky("ok");

    public static Task<string> ExceptionActionsSeeHandledExceptionsWhenAskedTo() =>
        Risky("invalid", Profile.AllExceptions);

    public static Task<string> ExceptionActionsSeeUnhandledExceptionsEitherWay() =>
        Risky("unsupported", Profile.AllExceptions);

    public static Task<string> ExceptionHandledForRequestWithoutResponse() =>
        Run.Scenario(services => Run.Timing(() => services.GetRequiredService<ISender>().Send(new Fragment())));

    public static Task<string> ExceptionHandledThroughSendObject() =>
        Run.Scenario(async services => await services.GetRequiredService<ISender>().Send((object)new Risky("argument")));

    private static Task<string> Risky(string mode, Profile profile = Profile.Plain) =>
        Run.Scenario(
            async services =>
            {
                try
                {
                    return await services.GetRequiredService<ISender>().Send(new Risky(mode));
                }
                catch (Exception exception)
                {
                    return $"threw {exception.GetType().Name}";
                }
            },
            profile: profile);
}
