using StreamingServiceServer.API.GraphQL.Schema;
using StreamingServiceServer.Data;

namespace StreamingServiceServer.API.GraphQL.Configuration;

public static class GraphQLConfiguration
{
    public static IServiceCollection AddGraphQLConfiguration(
        this IServiceCollection services)
    {
        var addGraphQlServer = services
            .AddGraphQLServer();
        addGraphQlServer.AddQueryType<Query>();
        addGraphQlServer.AddMutationType<Mutation>();
        addGraphQlServer.AddProjections();
        addGraphQlServer.AddFiltering();
        addGraphQlServer.AddSorting();
        addGraphQlServer.ModifyRequestOptions(opt => opt.IncludeExceptionDetails = true);

        return services;
    }
}