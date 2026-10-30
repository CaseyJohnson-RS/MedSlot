using System.Net;
using MedSlot.Integration.Tests.Infrastructure;

namespace MedSlot.Integration.Tests;

public class ApiSmokeTests(DatabaseFixture fixture)
{
    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")] // включает проверку подключения к Postgres
    [InlineData("/openapi/v1.json")]
    [InlineData("/scalar/")]
    public async Task Endpoint_responds_with_200(string url)
    {
        using var client = fixture.Factory.CreateClient();

        var response = await client.GetAsync(new Uri(url, UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
