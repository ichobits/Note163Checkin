﻿using Newtonsoft.Json.Linq;
using PuppeteerSharp;
using StackExchange.Redis;
using System.Text.Json;

HttpClient _scClient = null;
Conf _conf = Deserialize<Conf>(GetEnvValue("CONF"));
if (!string.IsNullOrWhiteSpace(_conf.ScKey))
{
    _scClient = new HttpClient();
}

#region redis

ConnectionMultiplexer redis = ConnectionMultiplexer.Connect($"{_conf.RdsServer},password={_conf.RdsPwd},name=Note163Checkin,defaultDatabase=0,allowadmin=true,abortConnect=false");
IDatabase db = redis.GetDatabase();
bool isRedis = db.IsConnected("test");
Console.WriteLine("redis:{0}", isRedis ? "有效" : "无效");

#endregion

Console.WriteLine("有道云笔记签到开始运行...");
for (int i = 0; i < _conf.Users.Length; i++)
{
    User user = _conf.Users[i];
    string title = $"账号 {i + 1}: {user.Task} ";
    Console.WriteLine($"共 {_conf.Users.Length} 个账号，正在运行{title}...");

    #region 获取cookie

    string cookie = string.Empty;
    bool isInvalid = true; string result = string.Empty;

    string redisKey = $"Note163_{user.Username}";
    if (isRedis)
    {
        var redisValue = await db.StringGetAsync(redisKey);
        if (redisValue.HasValue)
        {
            cookie = redisValue.ToString();
            (isInvalid, result) = await IsInvalid(cookie);
            Console.WriteLine("redis获取cookie,状态:{0}", isInvalid ? "无效" : "有效");
        }
    }

    if (isInvalid)
    {
        cookie = await Login(user.Username, user.Password);
        (isInvalid, result) = await IsInvalid(cookie);
        Console.WriteLine("login获取cookie,状态:{0}", isInvalid ? "无效" : "有效");
        if (isInvalid)
        {//Cookie失效
            await Notify($"{title}Cookie失效，请检查登录状态！", true);
            continue;
        }
    }

    if (isRedis)
    {
        Console.WriteLine($"redis更新cookie:{await db.StringSetAsync(redisKey, cookie)}");
    }

    #endregion

    using var client = new HttpClient();
    client.DefaultRequestHeaders.Add("User-Agent", "ynote-android");
    client.DefaultRequestHeaders.Add("Cookie", cookie);

    long space = 0;
    space += Deserialize<YdNoteRsp>(result).RewardSpace;

    //签到
    result = await (await client.PostAsync("https://note.youdao.com/yws/mapi/user?method=checkin", null))
       .Content.ReadAsStringAsync();
    space += Deserialize<YdNoteRsp>(result).Space;

    //看广告
    for (int j = 0; j < 3; j++)
    {
        result = await (await client.PostAsync("https://note.youdao.com/yws/mapi/user?method=adPrompt", null))
           .Content.ReadAsStringAsync();
        space += Deserialize<YdNoteRsp>(result).Space;
    }

    //看视频广告
    for (int j = 0; j < 3; j++)
    {
        result = await (await client.PostAsync("https://note.youdao.com/yws/mapi/user?method=adRandomPrompt", null))
           .Content.ReadAsStringAsync();
        space += Deserialize<YdNoteRsp>(result).Space;
    }

    await Notify($"有道云笔记{title}签到成功，共获得空间 {space / 1048576} M");
}
Console.WriteLine("签到运行完毕");

async Task<(bool isInvalid, string result)> IsInvalid(string cookie)
{
    using var client = new HttpClient();
    client.DefaultRequestHeaders.Add("User-Agent", "ynote-android");
    client.DefaultRequestHeaders.Add("Cookie", cookie);
    //每日打开客户端（即登陆）
    string result = await (await client.PostAsync("https://note.youdao.com/yws/api/daupromotion?method=sync", null))
        .Content.ReadAsStringAsync();
    return (result.Contains("error", StringComparison.OrdinalIgnoreCase), result);
}

async Task<string> Login(string username, string password)
{
    var launchOptions = new LaunchOptions
    {
        Headless = false,
        DefaultViewport = null,
        ExecutablePath = @"/usr/bin/google-chrome"
    };
    var browser = await Puppeteer.LaunchAsync(launchOptions);
    Page page = await browser.DefaultContext.NewPageAsync();

    await page.GoToAsync("https://note.youdao.com/web", 60_000);
    await page.WaitForSelectorAsync(".login-btn", new WaitForSelectorOptions { Visible = true });
    await page.TypeAsync(".login-username", username);
    await page.TypeAsync(".login-password", password);
    await Task.Delay(5_000);
    await page.ClickAsync(".login-btn");
    // await page.WaitForSelectorAsync("#flexible-right", new WaitForSelectorOptions { Visible = true });

    var client = await page.Target.CreateCDPSessionAsync();
    var ckObj = await client.SendAsync("Network.getAllCookies");
    var cks = ckObj.Value<JArray>("cookies")
        .Where(p => p.Value<string>("domain").Contains("note.youdao.com"))
        .Select(p => $"{p.Value<string>("name")}={p.Value<string>("value")}");

    await browser.DisposeAsync();
    return string.Join(';', cks);
}

async Task Notify(string msg, bool isFailed = false)
{
    Console.WriteLine(msg);
    if (_conf.ScType == "Always" || (isFailed && _conf.ScType == "Failed"))
    {
        await _scClient?.GetAsync($"https://sc.ftqq.com/{_conf.ScKey}.send?text={msg}");
    }
}

T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip
});

string GetEnvValue(string key) => Environment.GetEnvironmentVariable(key);

#region Conf

class Conf
{
    public User[] Users { get; set; }
    public string ScKey { get; set; }
    public string ScType { get; set; }
    public string RdsServer { get; set; }
    public string RdsPwd { get; set; }
}

class User
{
    public string Task { get; set; }
    public string Username { get; set; }
    public string Password { get; set; }
}

#endregion

class YdNoteRsp
{
    /// <summary>
    /// Sync奖励空间
    /// </summary>
    public int RewardSpace { get; set; }

    /// <summary>
    /// 其他奖励空间
    /// </summary>
    public int Space { get; set; }
}
