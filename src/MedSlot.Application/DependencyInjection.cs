using MedSlot.Application.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace MedSlot.Application;

public static class DependencyInjection
{
    /// <summary>
    /// Регистрирует все обработчики команд и запросов сборки, каждый — обёрнутым в логирующий декоратор.
    /// </summary>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        RegisterHandlers(services, typeof(ICommandHandler<,>), typeof(LoggingCommandHandlerDecorator<,>));
        RegisterHandlers(services, typeof(IQueryHandler<,>), typeof(LoggingQueryHandlerDecorator<,>));
        services.AddScoped<Waitlist.WaitlistOfferService>();
        return services;
    }

    private static void RegisterHandlers(IServiceCollection services, Type handlerDefinition, Type decoratorDefinition)
    {
        var handlers = AssemblyReference.Assembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false, IsGenericTypeDefinition: false })
            .SelectMany(t => t.GetInterfaces()
                .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == handlerDefinition)
                .Select(i => (Implementation: t, Interface: i)))
            .Where(h => !h.Implementation.IsGenericType); // сами декораторы

        foreach (var (implementation, handlerInterface) in handlers)
        {
            services.AddScoped(implementation);

            var decorator = decoratorDefinition.MakeGenericType(handlerInterface.GetGenericArguments());
            services.AddScoped(
                handlerInterface,
                sp => ActivatorUtilities.CreateInstance(sp, decorator, sp.GetRequiredService(implementation)));
        }
    }
}
