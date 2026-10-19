using MediatR;

namespace Leftover;

public sealed record Ping : IRequest<int>;
