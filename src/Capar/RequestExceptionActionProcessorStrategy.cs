namespace Capar;

/// <summary>Which exceptions <c>IRequestExceptionAction</c>s see.</summary>
public enum RequestExceptionActionProcessorStrategy
{
    /// <summary>
    /// Only exceptions no exception handler handled. The action behaviour sits outside the handler behaviour,
    /// so a handled exception never reaches it. The default, as in MediatR.
    /// </summary>
    ApplyForUnhandledExceptions,

    /// <summary>Every exception, handled or not. The action behaviour sits inside the handler behaviour.</summary>
    ApplyForAllExceptions,
}
