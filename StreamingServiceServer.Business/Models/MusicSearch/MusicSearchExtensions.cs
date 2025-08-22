using System.Reflection.Emit;
using System.Text.Json;
using System.Text.Json.Serialization;
using StreamingServiceServer.Data.Models;

namespace StreamingServiceServer.Business.Models.MusicSearch;


public static class MusicBrainzMapper
{
    public static Artist ToEntity(this ArtistDto dto)
    {
        var artist = new Artist
        {
            Id = dto.Id,
            Name = dto.Name,
            SortName = dto.SortName,
            Type = dto.Type,
            TypeId = dto.TypeId,
            Gender = dto.Gender,
            GenderId = dto.GenderId,
            Country = dto.Country,
        };
        

        if (dto.Aliases != null)
        {
            artist.Aliases = dto.Aliases.Select(alias => new ArtistAlias
            {
                Name = alias.Name,
                SortName = alias.SortName,
                Type = alias.Type,
                TypeId = alias.TypeId,
                Locale = alias.Locale,
                Primary = alias.Primary,
                BeginDate = alias.BeginDate,
                EndDate = alias.EndDate,
                ArtistId = artist.Id
            }).ToList();
        }

        if (dto.Tags != null)
        {
            artist.Tags = dto.Tags.Select(tag => new ArtistTag
            {
                Name = tag.Name,
                ArtistId = artist.Id
            }).ToList();
        }

        return artist;
    }

    public static Recording ToEntity(this RecordingDto dto)
    {
        var recording = new Recording
        {
            Id = dto.Id,
            Title = dto.Title,
            Length = dto.Length,
            Cover =  dto.Cover.Cover,
            SmallCover = dto.Cover.SmallCover,
            VerySmallCover = dto.Cover.VerySmallCover,
            PositionInAlbum = dto.PositionInAlbum,
        };

        if (dto.ArtistCredit != null)
        {
            recording.ArtistCredit = dto.ArtistCredit.Select(ac => new RecordingArtistCredit
            {
                Name = ac.Name,
                ArtistId = ac.Artist?.Id,
                RecordingId = recording.Id
            }).ToList();
        }

        if (dto.Releases != null && dto.Releases.Any())
        {
            recording.Release = dto.Releases.First().ToEntity();
        }

        return recording;
    }

    public static Release ToEntity(this ReleaseDto dto) =>
        new Release
        {
            Id = dto.Id,
            Title = dto.Title,
            Artist = dto.Artist.ToEntity(),
            Cover =  dto.Cover.Cover,
            SmallCover = dto.Cover.SmallCover,
            VerySmallCover = dto.Cover.VerySmallCover,
        };

    public static Release ToEntity(this ReleaseDto dto, ICollection<TrackDto> tracks)
    {
        return new Release
        {
            Id = dto.Id,
            Title = dto.Title,
            Recordings = tracks.Select(track=>track.Recording.ToEntity()).ToList(),
            Artist = dto.Artist.ToEntity(),
            Cover =  dto.Cover.Cover,
            SmallCover = dto.Cover.SmallCover,
            VerySmallCover = dto.Cover.VerySmallCover,
        };
    }

    public static RecordingResponse ToResponse(this Recording recording) =>
        new RecordingResponse
        {
            Id = recording.Id,
            Title = recording.Title,
            ArtistName = recording.Release.Artist.Name,
            ReleaseTitle = recording.Release?.Title ?? string.Empty,
            Cover =  recording.VerySmallCover,
            PositionInAlbum = recording.PositionInAlbum,
        };
    
    public static RecordingResponse ToResponse(this RecordingDto recording) =>
        new RecordingResponse
        {
            Id = recording.Id,
            Title = recording.Title,
            ArtistName = recording.ArtistCredit.FirstOrDefault()?.Artist.Name,
            ReleaseTitle = recording.Releases?.FirstOrDefault()?.Title,
            Cover =  recording.Cover.VerySmallCover,
            PositionInAlbum = recording.PositionInAlbum,
        };

    public static ReleaseResponse ToResponse(this ReleaseDto release) =>
        new ReleaseResponse
        {
            Id = release.Id,
            Title = release.Title,
            ArtistName = release.Artist?.Name ?? string.Empty,
            Cover = release.Cover.VerySmallCover
        };

    public static ReleaseResponse ToResponse(this Release release) =>
        new ReleaseResponse
        {
            Id = release.Id,
            Title = release.Title,
            ArtistName = release.Artist?.Name ?? string.Empty,
            Cover = release.Cover
        };
}
