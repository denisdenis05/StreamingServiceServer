using HotChocolate;
using StreamingServiceServer.API.GraphQL.Types.Music;
using StreamingServiceServer.Business.Services.MusicSearch;

namespace StreamingServiceServer.API.GraphQL.Schema;

public class Query
{
    public MusicQuery Music([Service] IMetadataService metadataService) 
        => new(metadataService);
}