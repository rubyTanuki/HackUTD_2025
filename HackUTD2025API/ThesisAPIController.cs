using Microsoft.AspNetCore.Mvc;

[ApiController]
public class ThesisAPIController : ControllerBase
{
    public ThesisAPIController()
    {
    
    }

    [HttpPost("getArticles")] // localhost:5269/getArticles
    public async Task<IActionResult> GetArticles([FromBody]Dictionary<string, object> json){
        //parse json into thesis string
        string thesis = json["thesis"].ToString();

        // ask gemini for key words
        
        // webscrape keywords on google scholar

        // take the top k results and turn them into article objects 

        // turn article list into JSON

        //return JSON

        return Ok(thesis);
    }
}




