# Shiny AspNet

A set of libraries for ASP.NET Core applications.

| Package | Description |
| --- | --- |
| `Shiny.AspNet.WebHosting` | ASP.NET modular web hosting with `IWebModule` |

## Web Hosting
* Merges service container build and post build scenarios into a single class using `IWebModule`

### Setup
1. Install the NuGet package `Shiny.AspNet.WebHosting`
2. Add a web module by implementing `IWebModule`:
   ```csharp
   using Shiny;

   public class MyWebModule : IWebModule
   {
       public void Add(WebApplicationBuilder builder)
       {
           // Register your services here
       }

       public void Use(WebApplication app)
       {
           // Configure your middleware/endpoints here
       }
   }
   ```
3. In your application hosting startup, add the following:
   ```csharp
   using Shiny;

   var builder = WebApplication.CreateBuilder(args);
   builder.AddInfrastructureModules(new MyWebModule());

   var app = builder.Build();
   app.UseInfrastructureModules();
   ```

> Previously shipped as `Shiny.Extensions.WebHosting` from [shinyorg/extensions](https://github.com/shinyorg/extensions). The API and `Shiny` namespace are unchanged — swap the package reference to migrate.
