using EPiServer.Configuration;
using EPiServer.Core;
using System;
using System.Security.Principal;
using System.Threading;
using System.Web;

namespace EPiServer.BurstCache
{
    /// <summary>
    /// Handles output cache settings. Used from <see cref="PageBase"/> in WebForms and <see cref="EPiServer.Web.Mvc.ContentOutputCacheAttribute"/> in MVC.
    /// </summary>
    /// <remarks>
    /// To use this one in WebForms, override PageBase's SetCachePolicy to call this one's SetCachePolicy instead of
    /// OutputCacheHandler's.
    /// </remarks>
    public class BurstOutputCacheHandler
    {
        /// <summary>
        /// Stores state to be passed to the Output Cache Validation Callback.
        /// You pass it to
        /// <see cref="HttpCachePolicyBase.AddValidationCallback(HttpCacheValidateHandler, object)"/> when you register
        /// <see cref="ValidateOutputCache(HttpContext, object, ref HttpValidationStatus)"/>.
        /// </summary>
        public class State
        {
            public readonly long VersionInCache;
            public long VersionRendering;
            public Settings Config { get; private set; }
            public DateTime RefreshAfter { get; private set; }

            public State(Settings settings, long currentVersion, DateTime refreshAfter)
            {
                Config = settings;
                VersionInCache = currentVersion;
                RefreshAfter = refreshAfter;
                VersionRendering = -1;
            }
        }

        /// <summary>
        /// Sets the cache policy for this request based on parameters as current user and parameters.
        /// </summary>
        /// <remarks>
        /// Override this method if you wish to customize the cache policy for a page.
        /// The output cache is turned on based on the following criteria:
        /// <para>
        /// 1. The <b>EPnCachePolicyTimeout</b> in web.config is > 0.
        /// </para>
        /// <para>
        /// 2. The current user must not be logged on, aka Anonymous.
        /// </para>
        /// <para>
        /// 3. The request must be a GET type request. Hence, Postbacks and form postings will not be cached.
        /// </para>
        /// <para>
        /// 4. The current page must be the published version (the WorkID is == 0).
        /// </para>
        /// The cache parameters are fetched from web.config, more specifically the <b>EPsCacheVaryByCustom</b> and 
        /// <B>EPsCacheVaryByParams</B> settings. Additionally, a dependency to the <b>DataFactoryCache</b> is set. 
        /// When pages are changed, the cache is flushed. Cache item expiration is set to the <b>HttpCacheExpiration</b> 
        /// setting, which is the number of seconds the item should reside in the cache, as long as the <b>StopPublish</b> 
        /// value of the page is not less than the policy timeout (in which case, the <b>StopPublish</b> value is used).
        /// </remarks>
        public virtual void SetCachePolicy(IPrincipal currentPrincipal, ContentReference currentContent, HttpContextBase httpContext, Settings configurationSettings, DateTime? contentExpiration, TimeSpan refresh)
        {
            if (UseOutputCache(currentPrincipal, httpContext, configurationSettings.HttpCacheExpiration) && currentContent.WorkID <= 0)
            {
                string cacheVaryByCustom = EPiServer.Configuration.Settings.Instance.HttpCacheVaryByCustom;

                httpContext.Response.Cache.SetVaryByCustom(cacheVaryByCustom);

                string[] cacheVaryByParams = EPiServer.Configuration.Settings.Instance.HttpCacheVaryByParams;
                foreach (string varyBy in cacheVaryByParams)
                {
                    httpContext.Response.Cache.VaryByParams[varyBy] = true;
                }

                httpContext.Response.Cache.SetValidUntilExpires(true);

                DateTime cacheExpiration = DateTime.Now + configurationSettings.HttpCacheExpiration;
                DateTime stopPublish = contentExpiration.HasValue ? contentExpiration.Value : DateTime.MaxValue;
                DateTime expiresAt = cacheExpiration < stopPublish ? cacheExpiration : stopPublish;

                var state = new State(configurationSettings, DataFactoryCache.Version, expiresAt.Subtract(refresh));
                httpContext.Response.Cache.AddValidationCallback(new HttpCacheValidateHandler(ValidateOutputCache), state);
                httpContext.Response.Cache.SetExpires(expiresAt);
                httpContext.Response.Cache.SetCacheability(EPiServer.Configuration.Settings.Instance.HttpCacheability);
            }
            else
            {
                //We dont use HttpCacheability.NoCache since it is not supported by all browsers.
                //instead we set a historical expiration date.
                httpContext.Response.Cache.SetExpires(DateTime.Now.AddDays(-1));
                httpContext.Response.Cache.SetCacheability(HttpCacheability.Private);
            }
        }

        /// <summary>
        /// Decides if the output cache should be used for this request.
        /// </summary>
        /// <param name="principal">The current principal.</param>
        /// <param name="context">The current context.</param>
        /// <param name="duration">The duration.</param>
        /// <returns></returns>
        public static bool UseOutputCache(IPrincipal principal, HttpContextBase context, TimeSpan duration)
        {
            return !principal.Identity.IsAuthenticated
                && duration != TimeSpan.Zero
                && String.Equals(context.Request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Checks if the output cache is still valid.
        /// </summary>
        /// <param name="context">The context.</param>
        /// <param name="data">The data, which is an instance of <see cref="State"/>.</param>
        /// <param name="validationStatus">The validation status.</param>
        public static void ValidateOutputCache(HttpContext context, object data, ref HttpValidationStatus validationStatus)
        {
            HttpContextWrapper httpContext = new HttpContextWrapper(context);
            KeepUserLoggedOn(httpContext);
            var state = (State)data;
            if (UseOutputCache(context.User, httpContext, state.Config.HttpCacheExpiration))
            {
                var currentVersion = DataFactoryCache.Version;

                if ((state.VersionInCache == currentVersion) && (state.RefreshAfter > DateTime.Now))
                {
                    // Valid version with remaining time before refresh
                    return;
                }

                if (Interlocked.Exchange(ref state.VersionRendering, currentVersion) == currentVersion)
                {
                    // Someone else have already received "IgnoreThisRequest" from this method for this state object.
                    // This time, serve a cached (though stale) response again.
                    return;
                }
                // This cache needs to be updated, and this request is the first one to ask for it.
            }
            validationStatus = HttpValidationStatus.IgnoreThisRequest;
        }

        /// <summary>
        /// Makes sure windows authentication stay persistent even on anonymous pages when user has been logged in
        /// </summary>
        private static void KeepUserLoggedOn(HttpContextBase httpContext)
        {
            if (!EPiServer.Security.FormsSettings.IsFormsAuthentication && EPiServer.Configuration.Settings.Instance.UIKeepUserLoggedOn)
            {
                if (HttpContext.Current.Request.Cookies["KeepLoggedOnUser"] != null)
                {
                    if (!httpContext.User.Identity.IsAuthenticated)
                    {
                        DefaultAccessDeniedHandler.CreateAccessDeniedDelegate()(null);
                    }
                }
                else if (httpContext.User.Identity.IsAuthenticated)
                {
                    HttpCookie keepLoggedOn = new HttpCookie("KeepLoggedOnUser", "True");
                    keepLoggedOn.Path = HttpContext.Current.Request.ApplicationPath;
                    HttpContext.Current.Response.Cookies.Add(keepLoggedOn);
                }
            }
        }
    }
}