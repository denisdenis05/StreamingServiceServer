using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StreamingServiceServer.Business.AudioAnalysis;

namespace StreamingServiceServer.API.Controllers;

[ApiController]
[Route("api/dashboard/settings/audio-analysis")]
[Authorize(Roles = AdministratorRole)]
public sealed class AudioAnalysisSettingsController : ControllerBase
{
    private const string AdministratorRole = "Admin";
    private const string ConflictMessage = "Audio analysis settings changed. Refresh them before saving again.";
    private const string ReadFailureMessage = "Audio analysis settings could not be loaded.";
    private const string SaveFailureMessage = "Audio analysis settings could not be saved.";
    private const string ConnectionFailureMessage = "The saved AudioMuse connection could not be verified.";
    private readonly IAudioAnalysisAdministrationStore administration;
    private readonly IAudioAnalysisSettingsStore configuration;
    private readonly IAudioAnalysisClient client;

    public AudioAnalysisSettingsController(IAudioAnalysisAdministrationStore administration,
        IAudioAnalysisSettingsStore configuration, IAudioAnalysisClient client)
    {
        this.administration = administration;
        this.configuration = configuration;
        this.client = client;
    }

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        try { return Ok(await administration.GetSettingsAsync(cancellationToken)); }
        catch (Exception failure) when (IsServiceFailure(failure))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = ReadFailureMessage });
        }
    }

    [HttpPut]
    public async Task<IActionResult> Put([FromBody] UpdateAudioAnalysisSettings update, CancellationToken cancellationToken)
    {
        try { AudioAnalysisSettingsValidation.Validate(update); }
        catch (AudioAnalysisSettingsValidationException failure) { return BadRequest(new { message = failure.Message }); }
        try
        {
            var result = await administration.UpdateSettingsAsync(update, cancellationToken);
            return result is null ? Conflict(new { message = ConflictMessage }) : Ok(result);
        }
        catch (AudioAnalysisSettingsValidationException failure) { return BadRequest(new { message = failure.Message }); }
        catch (Exception failure) when (IsServiceFailure(failure))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = SaveFailureMessage });
        }
    }

    [HttpPost("test")]
    public async Task<IActionResult> Test(CancellationToken cancellationToken)
    {
        try
        {
            var settings = await configuration.GetConfigurationAsync(cancellationToken);
            return await client.CheckHealthAsync(settings, cancellationToken) ? Ok(new { ready = true })
                : StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = ConnectionFailureMessage });
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = ConnectionFailureMessage });
        }
        catch (Exception failure) when (IsServiceFailure(failure))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = ConnectionFailureMessage });
        }
    }

    private static bool IsServiceFailure(Exception failure) => failure is not OperationCanceledException;
}
