using Feedarr.Api.Models;
using Feedarr.Api.Services.Titles;
using Feedarr.Api.Services.Matching;
using Feedarr.Api.Services.Posters;
using Feedarr.Api.Data.Repositories;

namespace Feedarr.Api.Tests;

public sealed class TitleParserP2Tests
{
    [Theory]
    [InlineData(UnifiedCategory.Spectacle,"Arnaud.Tsamere.Chose.Promise.2013.FRENCH.COMMENTARY.1080p.HDLight.AC3.5.1.H264-XTGMKV","Arnaud Tsamere Chose Promise",2013)]
    [InlineData(UnifiedCategory.Animation,"Asterix.The.Mansion.Of.The.Gods.Le.Domaine.des.Dieux.2014.MULTi.1080p.HDRip.AAC.2.0.x264-XTGMKV","Asterix The Mansion Of The Gods Le Domaine des Dieux",2014)]
    [InlineData(UnifiedCategory.Spectacle,"Les.Robins.Des.Bois.-.L'Instant.Norvegien.-.Divx.Fr.-.Nhadda.2001.VFF.SD.DVDRip.MPEG.Audio.2.0.DivX-Nhadda","Les Robins Des Bois - L'Instant Norvegien",2001)]
    [InlineData(UnifiedCategory.Animation,"Lily.C.A.T.1987.JAP.VOSTEN.480p.DVDRip.AAC.2.0.x264-NoTag","Lily C A T",1987)]
    [InlineData(UnifiedCategory.Film,"Moontrap.1989.VFCUT.MULTi.VFF.CUSTOM.1080p.BluRay.AC3.x264-eMux.FRENCH","Moontrap",1989)]
    [InlineData(UnifiedCategory.Animation,"How.the.Grinch.Stole.Christmas.The.Ultimate.Edition.1966.FR.EN.480p.WEB.H264-PiCKLES","How the Grinch Stole Christmas",1966)]
    public void AuditStandaloneWorksHaveDatedSearchTitles(UnifiedCategory category,string raw,string title,int year)
    {
        var parsed=new TitleParser().Parse(raw,category);
        Assert.Equal(title,parsed.TitleClean); Assert.Equal(year,parsed.Year);
        var query=Assert.Single(SearchCandidateGenerator.Generate(new(raw,category)));
        Assert.Equal(title,query.Title);Assert.Equal(CandidateYearEvidence.Parsed,query.YearEvidence);
        if(category is UnifiedCategory.Animation or UnifiedCategory.Spectacle)
        {
            var persisted=new ReleaseForPoster {Title=raw,TitleClean="obsolete",Year=null,IsDirectRss=true,MediaType="movie"};
            Assert.Equal("movie",PosterFetchService.ResolveVideoType(persisted,category));
        }
        if(raw.Contains("VFCUT")) Assert.Equal("VFCUT",parsed.Edition);
        if(raw.Contains("Ultimate")) Assert.Equal("The Ultimate Edition",parsed.Edition);
    }

    [Theory]
    [InlineData("Affaire.Priscilia.Gonzales.le.tueur.du.cimetiere")]
    [InlineData("Affaire.Yvette.Julien.trahison.familiale")]
    public void BroadcastSubjectAndYearDoNotDateProgramme(string subject)
    {
        var raw=$"Au.bout.de.lenquete.la.fin.du.crime.parfait.-.{subject}.2026.VOF.1080p.HDTV.AAC.2.0.H264";
        var parsed=new TitleParser().Parse(raw,UnifiedCategory.Emission);
        Assert.Equal("Au bout de lenquete la fin du crime parfait",parsed.TitleClean);
        Assert.Equal(subject.Replace('.',' '),parsed.EpisodeSubject);
        Assert.Equal(2026,parsed.BroadcastYear);Assert.Null(parsed.Year);Assert.Null(parsed.AirDate);
        Assert.All(SearchCandidateGenerator.Generate(new(raw,UnifiedCategory.Emission)),q=>Assert.Null(q.Year));
    }

    [Theory]
    [InlineData("Show.-.Reportage.Subject.2026.11.12.VOF.1080p.HDTV.H264", "Show", "Reportage Subject")]
    [InlineData("Show.2026.11.12.S01E02.1080p", "Show", null)]
    public void AirDateNeverBecomesProgrammeSearchYear(string raw,string title,string? subject)
    {
        var parsed=new TitleParser().Parse(raw,UnifiedCategory.Emission);
        Assert.Equal(title,parsed.TitleClean);Assert.Equal(subject,parsed.EpisodeSubject);
        Assert.Equal("2026-11-12",parsed.AirDate);Assert.Equal(2026,parsed.BroadcastYear);
        Assert.All(SearchCandidateGenerator.Generate(new(raw,UnifiedCategory.Emission)),q=>Assert.Null(q.Year));
    }

    [Theory]
    [InlineData("Cut.2020.1080p.WEB.H264", "Cut")]
    [InlineData("Commentary.2020.1080p.WEB.H264", "Commentary")]
    [InlineData("1917.2019.1080p.BluRay.x264", "1917")]
    [InlineData("F1.2025.1080p.WEB.H264", "F1")]
    [InlineData("Film.VFCUT.1080p", "Film VFCUT")]
    public void MetadataWordsAndNumbersNeedContext(string raw,string title)
        => Assert.Equal(title,new TitleParser().Parse(raw,UnifiedCategory.Film).TitleClean);

    [Fact]
    public void ProgrammeSeparatorAloneDoesNotProveEpisodeSubject()
    {
        var parsed=new TitleParser().Parse("Show.-.Another.Title.2026.VOF.1080p.HDTV.H264",UnifiedCategory.Emission);
        Assert.Equal("Show - Another Title",parsed.TitleClean);Assert.Null(parsed.EpisodeSubject);
    }

    [Fact]
    public void AlbatorSeasonUsesTvWithoutBecomingMovieAfterCleaning()
    {
        var row=new ReleaseForPoster {Title="Albator84.1982.S01.COMPLETE.VFF.DVDRiP.AVC-NoTag.FRENCH",IsDirectRss=true,MediaType="movie",CategoryIds="2000,102101"};
        Assert.Equal("tv",PosterFetchService.ResolveVideoType(row,UnifiedCategory.Animation));
    }
}
