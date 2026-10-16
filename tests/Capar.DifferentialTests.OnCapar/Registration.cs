using Capar.DifferentialTests.OnCapar;
using Microsoft.Extensions.DependencyInjection;

namespace Scenarios;

public static partial class Library
{
    static partial void Register(IServiceCollection services) => services.AddCapar();
}
