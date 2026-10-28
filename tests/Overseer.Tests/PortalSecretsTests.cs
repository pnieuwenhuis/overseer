using System.Reflection;
using Microsoft.AspNetCore.Components;
using Overseer.Secrets;

namespace Overseer.Tests;

public class PortalSecretsTests
{
    // A value, once entered, is never shown again: the Portal may set, remove and check secrets, never read them.
    [Fact]
    public void No_Portal_component_can_read_a_secret_value()
    {
        var components = typeof(ISecretReader).Assembly.GetTypes()
            .Where(type => typeof(IComponent).IsAssignableFrom(type) && !type.IsAbstract);

        var readers = components
            .Where(component =>
                component.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Any(property => property.IsDefined(typeof(InjectAttribute)) && property.PropertyType.IsAssignableTo(typeof(ISecretReader)))
                || component.GetConstructors()
                    .Any(constructor => constructor.GetParameters().Any(parameter => parameter.ParameterType.IsAssignableTo(typeof(ISecretReader)))))
            .Select(component => component.FullName);

        Assert.NotEmpty(components);
        Assert.Empty(readers);
    }
}
