using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace MedSlot.Integration.Tests;

public class ApiSmokeTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    [InlineData("/openapi/v1.json")]
    [InlineData("/scalar/")]
    public async Task Endpoint_responds_with_200(string url)
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri(url, UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
