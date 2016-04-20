using EPiServer;
using EPiServer.Configuration;
using EPiServer.Core;
using EPiServer.ServiceLocation;
using EPiServer.Web;
using EPiServer.Web.Routing;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Principal;
using System.Web;
using System.Web.Mvc;

namespace EPiServer.BurstCache
{
    /// <summary>
    /// Attribute that can be used to specify that the output from a action should be put in output cache.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This attribute cannot be used on child actions. The reason for this is that in MVC child actions are output cached in a memory cache
    /// and hence the output cache will not be invalidated when a Content is changed.
    /// </para>
    /// The output cache is turned on based on the following criteria:
    /// <para>
    /// 1. The <b>httpCacheExpiration</b> in web.config is > 0.
    /// </para>
    /// <para>
    /// 2. The current user must not be logged on, aka Anonymous.
    /// </para>
    /// <para>
    /// 3. The request must be a GET type request. Hence, Postbacks and form postings will not be cached.
    /// </para>
    /// <para>
    /// 4. The current content must be the published version (the WorkID is == 0).
    /// </para>
    /// <para>
    /// Some cache parameters are fetched from configuration if not present on attribute. More specifically if Duration is 0 then
    /// the <b>httpCacheExpiration</b> (on element/episerver/sites/site/SiteSetting) value from web.config is used. 
    /// If VaryByCustom is null or empty then <b>httpCacheVaryByCustom</b> (on element/episerver/sites/site/SiteSetting) value
    /// from web.config is used.
    /// Cache item expiration is set to the <b>httpCacheExpiration</b> 
    /// setting, which is the number of seconds the item should reside in the cache, as long as the <b>IVersionable.StopPublish</b> 
    /// value of the content is not less than the policy timeout (in which case, the <b>StopPublish</b> value is used).
    /// </para>
    /// </remarks>
    public class BurstContentOutputCacheAttribute : OutputCacheAttribute
    {
        private bool _initialized;

        /// <summary>
        /// Initializes a new instance of the <see cref="BurstContentOutputCacheAttribute"/> class.
        /// </summary>
        public BurstContentOutputCacheAttribute()
        {
            UseOutputCacheValidator = (principal, httpContext, duration) => BurstOutputCacheHandler.UseOutputCache(principal, httpContext, duration);
        }

        //Note: For attributes we should not take dependency through constructor since they are instantiated during assembly scanning

        /// <summary>
        /// Gets or sets the configuration settings.
        /// </summary>
        /// <value>The configuration settings.</value>
        public Injected<Settings> ConfigurationSettings { get; set; }

        /// <summary>
        /// Gets or sets the content loader.
        /// </summary>
        /// <value>The content loader.</value>
        public Injected<IContentLoader> ContentLoader { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether this <see cref="BurstContentOutputCacheAttribute"/> is disabled.
        /// </summary>
        /// <remarks>
        /// This value is mainly used when a baseclass specifies that output cache should be used, then a subclass
        /// can put the attribute with Disable=true to prevent output cache to be used for the subclass.
        /// </remarks>
        /// <value><c>true</c> if disable; otherwise, <c>false</c>.</value>
        public virtual bool Disable { get; set; }

        /// <summary>
        /// Gets or sets the number of seconds after which this cache is refreshed. Only one thread refreshes
        /// the cache, others will still serve the previous output cache provided it is still within its Duration period.
        /// </summary>
        /// <remarks>
        /// When unset (0), the value is considered to be 10 seconds less than <see cref="BurstContentOutputCacheAttribute.Duration"/>.
        /// Setting this property to a value equal to or higher than <see cref="BurstContentOutputCacheAttribute.Duration"/>,
        /// disables the functionality provided by Refresh.
        /// </remarks>
        public virtual int Refresh { get; set; }

        /// <summary>
        /// Gets or sets the use output cache validator.
        /// </summary>
        /// <remarks>
        /// This is the function that is called to evaluate if a request should be added to output cache. 
        /// The default implementation calls <see cref="BurstOutputCacheHandler.UseOutputCache"/>.
        /// </remarks>
        /// <value>The use output cache validator.</value>
        public virtual Func<IPrincipal, HttpContextBase, TimeSpan, bool> UseOutputCacheValidator { get; set; }

        /// <summary>
        /// This method is an implementation of <see cref="M:System.Web.Mvc.IActionFilter.OnActionExecuting(System.Web.Mvc.ActionExecutingContext)"/> and supports the ASP.NET MVC infrastructure. It is not intended to be used directly from your code.
        /// </summary>
        /// <param name="filterContext">The filter context.</param>
        public override void OnActionExecuting(ActionExecutingContext filterContext)
        {
            if (filterContext.IsChildAction)
            {
                throw new NotSupportedException("BurstContentOutputCacheAttribute should not be used on child actions.");
            }
            base.OnActionExecuting(filterContext);
        }

        /// <summary>
        /// Called before the action result executes.
        /// </summary>
        /// <param name="filterContext">The filter context, which encapsulates information for using <see cref="T:System.Web.Mvc.AuthorizeAttribute"/>.</param>
        /// <exception cref="T:System.ArgumentNullException">The <paramref name="filterContext"/> parameter is null.</exception>
        public override void OnResultExecuting(ResultExecutingContext filterContext)
        {
            if (Disable)
            {
                return;
            }

            if (!_initialized)
            {
                //If no values are set on attrbute take default values from config
                if (Duration == 0)
                {
                    Duration = Convert.ToInt32(ConfigurationSettings.Service.HttpCacheExpiration.TotalSeconds);
                }
                if (String.IsNullOrEmpty(VaryByCustom))
                {
                    VaryByCustom = ConfigurationSettings.Service.HttpCacheVaryByCustom;
                }
                _initialized = true;
            }

            var contentLink = filterContext.RequestContext.GetContentLink();
            if (!ContentReference.IsNullOrEmpty(contentLink))
            {
                DateTime now = DateTime.Now;

                IVersionable versionable = ContentLoader.Service.Get<IContent>(contentLink) as IVersionable;
                if (versionable != null && versionable.StopPublish.HasValue)
                {
                    if (versionable.StopPublish < now)
                    {
                        return;
                    }

                    if (versionable.StopPublish.Value < now.AddSeconds(Duration))
                    {
                        Duration = Convert.ToInt32((versionable.StopPublish.Value - now).TotalSeconds);
                    }
                }

                var duration = new TimeSpan(0, 0, Duration);
                var refresh = new TimeSpan(0, 0, Refresh == 0 ? Math.Max(0, Duration - 10) : Refresh);

                if (UseOutputCacheValidator(filterContext.HttpContext.User, filterContext.HttpContext, duration))
                {
                    base.OnResultExecuting(filterContext);
                    filterContext.HttpContext.Response.Cache.AddValidationCallback(new HttpCacheValidateHandler(BurstOutputCacheHandler.ValidateOutputCache),
                        new BurstOutputCacheHandler.State(
                            ConfigurationSettings.Service,
                            DataFactoryCache.Version,
                            now + refresh));
                }
            }
        }

        

        
    }
}