using Microsoft.AspNetCore.Mvc;

namespace StreamingServiceServer.API.Controllers;

[ApiController]
[Route("[controller]")]
public class StreamController : ControllerBase
{
    private readonly IConfiguration _configuration;
    
    public StreamController(ILogger<StreamController> logger, IConfiguration configuration)
    {
        _configuration = configuration;
    }
    
    [HttpGet]
    public IActionResult StreamAudio([FromQuery] string id)
    {
        Console.WriteLine("hI");
        var audioPath = $"{_configuration["Music:Path"]}{id}.mp3";

        if (!System.IO.File.Exists(audioPath))
            return NotFound();

        var mime = "audio/mpeg"; 

        return PhysicalFile(audioPath, mime, enableRangeProcessing: true);
    }
}