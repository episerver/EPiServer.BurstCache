# EPiServer.BurstCache
An output cache module for WebForms and MVC that allows serving of stale responses while an updated response is generated.

## MVC
For MVC implementations, simply tack on the BurstOutputCache attribute to your controller like so:
```
[BurstContentOutputCacheAttribute(Duration = 600, Refresh = 60, Location = System.Web.UI.OutputCacheLocation.Server)]
```
The Refresh property tells the burst cache to refresh the cached data with this interval. Duration means to never ever 
serve data older than that. It doesn't make sense to have the Refresh interval the same or greater than Duration.

## WebForms
For WebForms implementations, override the SetCachePolicy method of EPiServer.TemplatePage by (typically) adding
```
protected override void SetCachePolicy()
{
    Locate.Advanced.GetInstance<BurstOutputCacheHandler>().SetCachePolicy(PrincipalInfo.CurrentPrincipal,
        CurrentPageLink, new HttpContextWrapper(Context),
        Settings.Instance, new DateTime?((CurrentPage != null) ? CurrentPage.StopPublish : DateTime.MaxValue),
        new TimeSpan(0, 0, 60)); // This is the refresh interval; when this time has elapsed, one thread is chosen to refresh the page.
}
```
...to your ```SiteTemplatePage<T>```-class (if you're building an Alloy-based page.)
