using System.Diagnostics;
using System.Net.Http.Json;
using System.Numerics;
using HtmlAgilityPack;

using System.Text.Json;
using System.Text.Json.Serialization;


namespace KfChatDotNetBot;


internal class Utils
{
	private static readonly HttpClient _cl = new();
	private static string IMGBB_API_KEY = "";

	internal class ImgBBData
	{

		[JsonPropertyName("url")]
		public string? Url { get; set; }
    }

    internal class ImgBBResponse
	{
		[JsonPropertyName("data")]
		public ImgBBData? Data { get; set; }
    }

    public async static Task<string> LitterBoxUploadAsync(string fileName, byte[] data)
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent("72h"), "time" },
            { new StringContent("fileupload"), "reqtype" },
            { new ByteArrayContent(data), "fileToUpload", fileName }
        };

        using var r = await _cl.PostAsync("https://litterbox.catbox.moe/resources/internals/api.php", form);
        if (!r.IsSuccessStatusCode)
            throw new Exception("nigger");
        return await r.Content.ReadAsStringAsync();
    }

    public async static Task<string> LitterBoxUploadAsync(string fileName, Stream stream)
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent("72h"), "time" },
            { new StringContent("fileupload"), "reqtype" },
            { new StreamContent(stream), "fileToUpload", fileName }
        };

        using var r = await _cl.PostAsync("https://litterbox.catbox.moe/resources/internals/api.php", form);
        if (!r.IsSuccessStatusCode)
            throw new Exception("nigger");
        return await r.Content.ReadAsStringAsync();
    }

    public async static Task<string> PostImageUploadAsync(string fileName, Stream stream)
    {
        // i lied, it's actually imgbb now
        if (IMGBB_API_KEY == "")
		{
			IMGBB_API_KEY = Environment.GetEnvironmentVariable("IMGBB_API_KEY") ?? "";
			if (IMGBB_API_KEY == "")
				throw new Exception("IMGBB_API_KEY not set");
        }
		
        string url = $"https://api.imgbb.com/1/upload?expiration={60 * 60 * 4}&key={IMGBB_API_KEY}";

        using (var content = new MultipartFormDataContent())
        using (var imageContent = new StreamContent(stream))
        {
            // ImgBB expects the field name to be "image"
            content.Add(imageContent, "image", "upload.jpg");

            HttpResponseMessage response = await _cl.PostAsync(url, content);
            string json = await response.Content.ReadAsStringAsync();
			var imgbbResponse = JsonSerializer.Deserialize<ImgBBResponse>(json);
			if (imgbbResponse == null || imgbbResponse.Data == null || imgbbResponse.Data.Url == null)
			{
				throw new Exception("Failed to upload image to ImgBB");
            }
			return imgbbResponse.Data.Url;
        }
    }

    public async static Task<string> PostImageUploadAsync(string fileName, byte[] data)
	{
        using var form = new MultipartFormDataContent
		{
			{ new StringContent(Guid.NewGuid().ToString()), "upload_session" },
			{ new StringContent("1"), "numfiles" },

			{ new StringContent("0"), "optsize" },
			{ new StringContent(((DateTimeOffset)DateTime.UtcNow).ToUnixTimeMilliseconds().ToString()), "session_upload" },
			{ new StringContent(""), "gallery" },
			{ new StringContent("1"), "expire" },
			{ new ByteArrayContent(data), "file", fileName }
		};

		using var r = await _cl.PostAsync("https://postimages.org/json/rr", form);
		if (!r.IsSuccessStatusCode)
			throw new Exception("nigger");
		var url = (await r.Content.ReadFromJsonAsync<JsonElement>()!).GetProperty("url")!.GetString()!;
		var parts = url.Split('/');

		var retString = await _cl.GetStringAsync($"https://postimg.cc/{parts[3]}");
		var doc = new HtmlDocument();
		doc.LoadHtml(retString);

		

		//return $"https://postimg.cc/{parts[3]}";
		return doc.DocumentNode.Descendants("meta").Where(x => x.GetAttributeValue("property", "null") == "og:image").FirstOrDefault()!.GetAttributeValue("content", ""); ;
	}

}
