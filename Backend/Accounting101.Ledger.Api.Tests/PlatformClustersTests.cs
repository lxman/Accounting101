using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Ledger.Api.Tests;

public sealed class PlatformClustersTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private HttpClient Operator() => fixture.ClientFor(Guid.NewGuid(), "Operator", ("platform", "true"));

    [Fact]
    public async Task Registers_and_lists_clusters_without_leaking_connection_strings()
    {
        const string secret = "mongodb://secret-host:27017/very-secret";
        HttpResponseMessage reg = await Operator().PostAsJsonAsync(
            "/platform/clusters", new RegisterClusterRequest("cluster-2", secret));
        Assert.Equal(HttpStatusCode.Created, reg.StatusCode);

        HttpResponseMessage list = await Operator().GetAsync("/platform/clusters");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);

        string body = await list.Content.ReadAsStringAsync();
        List<ClusterResponse> clusters = JsonSerializer.Deserialize<List<ClusterResponse>>(
            body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        Assert.Contains(clusters, c => c.Key == "cluster-2" && c.HasConnectionString);
        Assert.Contains(clusters, c => c.Key == "default");
        // Redaction: the raw connection string is never present in the response body.
        Assert.DoesNotContain("secret-host", body);
    }

    [Fact]
    public async Task Register_cluster_requires_key_and_connection_string()
    {
        HttpResponseMessage resp = await Operator().PostAsJsonAsync(
            "/platform/clusters", new RegisterClusterRequest("", "mongodb://x"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }
}
