using System;
using System.Web.Optimization;

/// <summary>
/// Summary description for BundleConfig
/// </summary>
public class BundleConfig
{
    public static void RegisterBundles(BundleCollection bundles)
    {
        // new admin bundles
        bundles.IgnoreList.Clear();

        bundles.Add(
          new StyleBundle("~/css")
            //.Include("~/Content/ie10mobile.css")
            .Include("~/Content/bootstrap.min.css")
            .Include("~/Content/toastr.min.css")
            .Include("~/Content/font-awesome.min.css")
            .Include("~/Content/app.css")
            .Include("~/Content/angular.rangeSlider.min.css")
            .Include("~/Content/flags.css")
          );

        bundles.Add(
          new ScriptBundle("~/js")
            .Include("~/scripts/jquery-3.7.1.min.js")
            .Include("~/scripts/toastr.js")
            .Include("~/Scripts/angular.js")
            .Include("~/Scripts/angular-route.min.js")
            .Include("~/Scripts/angular-animate.min.js")
            .Include("~/Scripts/angular-sanitize.min.js")
            .Include("~/app.min.js")
            .Include("~/controllers/common.js")
            .Include("~/controllers/chataccounts.js")
            .Include("~/scripts/bootstrap.js")
            .Include("~/scripts/moment.js")
            .Include("~/scripts/angular.rangeSlider.js")
            .Include("~/scripts/angularjs-dropdown-multiselect.min.js")
          );
    }


}
