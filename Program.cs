using System;
using System.Threading.Tasks;
using Flurl.Http;
using Flurl.Http.Content;
using Flurl;
using CommandLine;
using System.IO;
using Serilog;
using System.Data;
using System.Xml.Linq;
using System.Xml.XPath;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.IO.Compression;
using Spectre.Console;
using Microsoft.Playwright;

namespace BatchDiagramInsertion;

class Options
{
    [Option('v', "vendorUserName", Required = true, HelpText = "Vendor User Name to authenticate against")]
    public String vendorUserName { get; set; }

    [Option('x', "vendorPassword", Required = true, HelpText = "Password for Vendor")]
    public String vendorPassword { get; set; }

    [Option('u', "userName", Required = true, HelpText = "User Name to authenticate against")]
    public String userName { get; set; }

    [Option('p', "password", Required = true, HelpText = "Password for User")]
    public String password { get; set; }

    [Option('i', "input", Required = true, HelpText = "DataSet file containing the diagrams")]
    public String inputFile { get; set; }

    [Option('a', "api", Required = true, HelpText = "Api to pull from")]
    public String api { get; set; }

    [Option('w', "ecrash web URL", Required = true, HelpText = "specific url for eCrash instance")]
    public String eCrashAddress { get; set; }
}
class Program
{
    private static readonly HttpClient client = new HttpClient();
    private static string? _oAuthToken;
    private static Options options;
    static async Task Main(string[] args)
    {
        await Parser.Default.ParseArguments<Options>(args)
            .MapResult(RunAndReturnExitCodeAsync, _ => Task.FromResult(1));
    }
    static async Task<int> RunAndReturnExitCodeAsync(Options arguments)
    {
        options = arguments;
        Log.Logger = new LoggerConfiguration()
        .MinimumLevel.Debug()
        .WriteTo.Console()
        .WriteTo.File("logs/log.txt", rollingInterval: RollingInterval.Day)
        .CreateLogger();


        //First step grab vendor token using vendor login
        await getToken(options);
        //Read list of diagrams 
        var diagrams = await readDiagrams(options.inputFile);
        List<Diagram> alreadyConvertedDiagrams = new List<Diagram>();
        //check to see if we have any of these diagrams in the output folder
        if(Directory.Exists(Path.Combine(Environment.CurrentDirectory,"convertedDiagrams"))){
            var alreadyConvertedDiagramFiles =  Directory.GetFiles(Path.Combine(Environment.CurrentDirectory,"convertedDiagrams")).ToList<string>();
            var fileNames = alreadyConvertedDiagramFiles.Select(x => Path.GetFileName(x));
            diagrams = diagrams.Where(x => !fileNames.Contains(x.name + ".svg")).ToList();
            alreadyConvertedDiagrams = alreadyConvertedDiagramFiles.Select(x=> new Diagram {
                diagram= File.ReadAllBytes(x),
                name= Path.GetFileName(x)
            }).ToList();
        }else{
            Directory.CreateDirectory(Path.Combine(Environment.CurrentDirectory,"convertedDiagrams"));
        }


        List<Diagram> convertedDiagrams = await AnsiConsole.Progress()
        .StartAsync(async ctx =>
        {
            var task = ctx.AddTask("Convert Diagrams", new ProgressTaskSettings
            {
                AutoStart = false
            });
            //converts diagrams from old to new format
            return await convertDiagrams(diagrams.ToList(), task);
        });

        List<Diagram> totalConvertedDiagrams = convertedDiagrams.Union(alreadyConvertedDiagrams).ToList();

        await AnsiConsole.Progress()
        .StartAsync(async ctx =>
        {
            var task = ctx.AddTask("Upload Diagrams", new ProgressTaskSettings
            {
                AutoStart = false
            });

            //uploads the diagrams to ecrash.la.gov/agency
            await UploadDiagrams(totalConvertedDiagrams, task);
        });

        AnsiConsole.Write(new Markup("[bold dodgerblue2]Diagram Templates successfully uploaded! :)[/]\n"));
        return 0;
    }

    private static async Task getToken(Options options)
    {
        try
        {
            var response = await options.api.AppendPathSegment("vendor/token/v1").PostJsonAsync(new { username = options.vendorUserName, password = options.vendorPassword }).ReceiveString();
            _oAuthToken = response;
            AnsiConsole.Write(new Markup("[bold dodgerblue2]Successfully Logged In and got Token from provider[/]\n"));
        }
        catch (Exception e)
        {
            Console.WriteLine(e.ToString());
        }
    }

    private static async Task<Diagram> modernizeDiagram(Diagram diagram)
    {
        Stream stream = new MemoryStream(diagram.diagram);
        await using var zipStream = await options.api.AppendPathSegment("vendor/diagram/v1/modernize")
            .WithOAuthBearerToken(_oAuthToken)
            .PostAsync(new ByteArrayContent(diagram.diagram))
            .ReceiveStream();
        using var zipFile = new ZipArchive(zipStream);
        var file = zipFile.Entries[0];
        await using var entryStream = file.Open();
        MemoryStream data = new MemoryStream();
        await entryStream.CopyToAsync(data);

        await File.WriteAllBytesAsync(Path.Combine(Environment.CurrentDirectory,"convertedDiagrams",diagram.name + ".svg"), data.ToArray());

        return new Diagram
        {
            diagram = data.ToArray(),
            name = diagram.name
        };
    }

    private static async Task<List<Diagram>> readDiagrams(string filepath)
    {
        var doc = XDocument.Load(filepath);
        var list = doc.XPathSelectElements("//DEPICTION_TB")
        .Select(element => new Diagram
        {
            name = element.Element("SHORT_NAME").Value == ""?  element.Element("PRI_ROAD").Value : element.Element("SHORT_NAME").Value,
            diagram = Convert.FromBase64String(element.Element("DIAGRAM").Value),
        }).ToList();
        return list;
    }

    private static async Task<List<Diagram>> convertDiagrams(List<Diagram> diagrams, ProgressTask task)
    {
        using var slim = new SemaphoreSlim(5);
        double incrementValue = (double)100 / diagrams.Count;
        var tasks = diagrams.Select(async diagram =>
        {
            await slim.WaitAsync();
            try
            {
                task.Increment(incrementValue);
                return await modernizeDiagram(diagram);
            }
            finally
            {
                slim.Release();
            }
        });
        var results = await Task.WhenAll(tasks);
        AnsiConsole.Write(new Markup("[bold dodgerblue2]Modernized Diagrams[/]"));
        return results.ToList();
    }

    private static async Task UploadDiagrams(List<Diagram> diagrams, ProgressTask task)
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
            //SlowMo = 50,
        });
        //login to ecrash.la.gov and save login cookies
        await LoginToECrash(browser);
        AnsiConsole.Write(new Markup("[bold dodgerblue2]Logged In to eCrash[/]"));

        //read login cookies and create new browser context from cookies
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            StorageStatePath = ".\\state.json"
        });
        double incrementer = 100 / (double)diagrams.Count;
        using var slim = new SemaphoreSlim(5);
        //loop through diagrams and upload
        var tasks = diagrams.Select(async diagram =>
        {
            await slim.WaitAsync();
            try
            {
                //uplod individual diagram
                await UploadSingleDiagram(diagram, context);
                //once this one is uploaded increment progress
                task.Increment(incrementer);
            }
            finally
            {
                slim.Release();
            }
        });
        await Task.WhenAll(tasks);
        await browser.CloseAsync();
    }

    private static async Task UploadSingleDiagram(Diagram diagram, IBrowserContext context)
    {
        var page = await context.NewPageAsync();
        await page.GotoAsync(options.eCrashAddress + "/Agency");
        await page.FillAsync("#Group", "Potato");
        await page.FillAsync("#Name", diagram.name.Replace('\\','-'));
        await page.SetInputFilesAsync("input#File", new FilePayload
        {
            Name = String.Format("{0}.svg",diagram.name.Replace('\\','-')),
            MimeType = "application/octet-stream",
            Buffer = diagram.diagram
        });
        //await page.ClickAsync("text=Upload");
        await page.CloseAsync();
    }

    private static async Task LoginToECrash(IBrowser browser)
    {
        var page = await browser.NewPageAsync();
        await page.GotoAsync(options.eCrashAddress);
        await page.FillAsync("input[name=Username]", options.userName);
        await page.FillAsync("input[name=Password]", options.password);
        await page.RunAndWaitForNavigationAsync(async () =>
        {
            await page.ClickAsync("text=Login");
        });
        await page.Context.StorageStateAsync(new BrowserContextStorageStateOptions
        {
            Path = "state.json"
        });
        await page.Context.DisposeAsync();
    }

}


class Diagram
{
    public string name { get; set; }
    public byte[] diagram { get; set; }
}
