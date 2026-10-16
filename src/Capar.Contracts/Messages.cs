namespace Capar;

/// <summary>Marker shared by every request, with or without a response.</summary>
public interface IBaseRequest;

/// <summary>A request that returns nothing.</summary>
public interface IRequest : IBaseRequest;

/// <summary>A request that returns a <typeparamref name="TResponse"/>.</summary>
/// <typeparam name="TResponse">What the handler returns.</typeparam>
public interface IRequest<out TResponse> : IBaseRequest;

/// <summary>A message delivered to every handler registered for it, which return nothing.</summary>
public interface INotification;

/// <summary>A request whose handler returns a stream of <typeparamref name="TResponse"/>.</summary>
/// <typeparam name="TResponse">The type of each item in the stream.</typeparam>
public interface IStreamRequest<out TResponse>;
