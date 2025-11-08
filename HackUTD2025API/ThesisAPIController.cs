using Microsoft.AspNetCore.Mvc;
using GenerativeAI;
using System.IO;

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
        
        string API_KEY =  System.IO.File.ReadAllText("key.txt");
        // ask gemini for key words
        var model = new GenerativeModel(
            apiKey: API_KEY,
            model: "models/gemini-2.5-flash"
        );
        string prompt = "return nothing but the best keywords for finding supporting resources on google scholar for this thesis in the format keyword1, keyword2 : " + thesis;
        var response = await model.GenerateContentAsync(prompt);
        
        // webscrape keywords on google scholar

        // take the top k results and turn them into article objects 

        // turn article list into JSON

        //return JSON

        return Ok(response.Text);
    }
}




