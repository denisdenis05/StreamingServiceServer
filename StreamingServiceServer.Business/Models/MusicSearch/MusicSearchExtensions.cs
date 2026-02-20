using StreamingServiceServer.Business.Services.Images;
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

    public static Recording ToEntity(this RecordingDto dto, IImageService imageService)
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
            recording.Release = dto.Releases.First().ToEntity(imageService);
        }

        if (string.IsNullOrEmpty(recording.Cover))
        {
            var releaseId = dto.Releases.FirstOrDefault()?.Id;
            if (releaseId != null)
            {
                var covers = imageService.GetAlbumCover(releaseId.Value);
                recording.Cover = covers.Cover;
                recording.SmallCover = covers.SmallCover;
                recording.VerySmallCover = covers.VerySmallCover;
            }
        }

        return recording;
    }

    public static Release ToEntity(this ReleaseDto dto, IImageService imageService)
    {
        var release = new Release
        {
            Id = dto.Id,
            Title = dto.Title,
            Artist = dto.Artist.ToEntity(),
            Cover =  dto.Cover.Cover,
            SmallCover = dto.Cover.SmallCover,
            VerySmallCover = dto.Cover.VerySmallCover,
        };

        if (string.IsNullOrEmpty(release.Cover))
        {
            var covers = imageService.GetAlbumCover(dto.Id);
            release.Cover = covers.Cover;
            release.SmallCover = covers.SmallCover;
            release.VerySmallCover = covers.VerySmallCover;
        }

        return release;
    }

    public static Release ToEntity(this ReleaseDto dto, ICollection<TrackDto> tracks, IImageService imageService)
    {
        return new Release
        {
            Id = dto.Id,
            Title = dto.Title,
            Recordings = tracks.Select(track=>track.Recording.ToEntity(imageService)).ToList(),
            Artist = dto.Artist.ToEntity(),
            Cover =  dto.Cover.Cover,
            SmallCover = dto.Cover.SmallCover,
            VerySmallCover = dto.Cover.VerySmallCover,
        };
    }

    public static RecordingResponse ToResponse(this Recording recording, IImageService imageService, bool isLiked = false)
    {
        var response = new RecordingResponse
        {
            Id = recording.Id,
            ReleaseId = recording.Release?.Id,
            Title = recording.Title,
            ArtistName = recording.Release.Artist.Name,
            ReleaseTitle = recording.Release?.Title ?? string.Empty,
            Cover = !string.IsNullOrEmpty(recording.LocalCover) ? recording.LocalCover : (!string.IsNullOrEmpty(recording.OptimizedCover) ? recording.OptimizedCover : recording.Cover),
            SmallCover = !string.IsNullOrEmpty(recording.LocalSmallCover) ? recording.LocalSmallCover : (!string.IsNullOrEmpty(recording.OptimizedSmallCover) ? recording.OptimizedSmallCover : recording.SmallCover),
            VerySmallCover = !string.IsNullOrEmpty(recording.LocalVerySmallCover) ? recording.LocalVerySmallCover : (!string.IsNullOrEmpty(recording.OptimizedVerySmallCover) ? recording.OptimizedVerySmallCover : recording.VerySmallCover),
            PositionInAlbum = recording.PositionInAlbum,
            IsLiked = isLiked
        };

        if (string.IsNullOrEmpty(response.Cover))
        {
            var covers = imageService.GetAlbumCover(recording.Release.Id);
            response.Cover = covers.Cover;
            response.SmallCover = covers.SmallCover;
            response.VerySmallCover = covers.VerySmallCover;
        }

        return response;
    }
    
    public static RecordingResponse ToResponse(this RecordingDto recording, IImageService imageService, bool isLiked = false)
    {
        var response = new RecordingResponse
        {
            Id = recording.Id,
            ReleaseId = recording.Releases?.FirstOrDefault()?.Id,
            Title = recording.Title,
            ArtistName = recording.ArtistCredit.FirstOrDefault()?.Artist.Name,
            ReleaseTitle = recording.Releases?.FirstOrDefault()?.Title,
            Cover =  recording.Cover.Cover,
            SmallCover =  recording.Cover.SmallCover,
            VerySmallCover =  recording.Cover.VerySmallCover,
            PositionInAlbum = recording.PositionInAlbum,
            IsLiked = isLiked
        };

        if (string.IsNullOrEmpty(response.Cover))
        {
            var releaseId = recording.Releases?.FirstOrDefault()?.Id;
            if (releaseId != null)
            {
                var covers = imageService.GetAlbumCover(releaseId.Value);
                response.Cover = covers.Cover;
                response.SmallCover = covers.SmallCover;
                response.VerySmallCover = covers.VerySmallCover;
            }
        }

        return response;
    }

    public static ReleaseResponse ToResponse(this ReleaseDto release, IImageService imageService)
    {
        var response = new ReleaseResponse
        {
            Id = release.Id,
            Title = release.Title,
            ArtistName = release.Artist?.Name
                         ?? release.ArtistCredit?.FirstOrDefault()?.Artist?.Name
                         ?? string.Empty,
            Cover = release.Cover.Cover,
            SmallCover = release.Cover.SmallCover,
            VerySmallCover = release.Cover.VerySmallCover
        };

        if (string.IsNullOrEmpty(response.Cover))
        {
            var covers = imageService.GetAlbumCover(release.Id);
            response.Cover = covers.Cover;
            response.SmallCover = covers.SmallCover;
            response.VerySmallCover = covers.VerySmallCover;
        }

        return response;
    }

    public static ReleaseResponse ToResponse(this Release release, IImageService imageService)
    {
        var response = new ReleaseResponse
        {
            Id = release.Id,
            Title = release.Title,
            ArtistName = release.Artist?.Name ?? string.Empty,
            Cover = !string.IsNullOrEmpty(release.LocalCover) ? release.LocalCover : (!string.IsNullOrEmpty(release.OptimizedCover) ? release.OptimizedCover : release.Cover),
            SmallCover = !string.IsNullOrEmpty(release.LocalSmallCover) ? release.LocalSmallCover : (!string.IsNullOrEmpty(release.OptimizedSmallCover) ? release.OptimizedSmallCover : release.SmallCover),
            VerySmallCover = !string.IsNullOrEmpty(release.LocalVerySmallCover) ? release.LocalVerySmallCover : (!string.IsNullOrEmpty(release.OptimizedVerySmallCover) ? release.OptimizedVerySmallCover : release.VerySmallCover)
        };

        if (string.IsNullOrEmpty(response.Cover))
        {
            var covers = imageService.GetAlbumCover(release.Id);
            response.Cover = covers.Cover;
            response.SmallCover = covers.SmallCover;
            response.VerySmallCover = covers.VerySmallCover;
        }

        return response;
    }

    public static ArtistResponse ToResponse(this ArtistDto artist) =>
        new ArtistResponse
        {
            Id = artist.Id,
            Name = artist.Name,
        };

    public static ArtistResponse ToResponse(this Artist artist) =>
        new ArtistResponse
        {
            Id = artist.Id,
            Name = artist.Name,
        };
}
