﻿using Microsoft.SharePoint.Client;
using OfficeDevPnP.Core.Pages;
using SharePoint.Modernization.Framework.Entities;
using SharePoint.Modernization.Framework.Functions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;

namespace SharePoint.Modernization.Framework.Transform
{
    /// <summary>
    /// Transforms content from "classic" page to modern client side page
    /// </summary>
    public class ContentTransformator: IContentTransformator
    {
        private ClientSidePage page;
        private PageTransformation pageTransformation;
        private FunctionProcessor functionProcessor;
        private List<CombinedMapping> combinedMappinglist;
        private Dictionary<string, string> siteTokens;

        class CombinedMapping
        {
            public int Order { get; set; }
            public ClientSideText ClientSideText { get; set; }
            public ClientSideWebPart ClientSideWebPart { get; set; }
        }

        #region Construction
        /// <summary>
        /// Instantiates the content transformator
        /// </summary>
        /// <param name="page">Client side page that will be updates</param>
        /// <param name="pageTransformation">Transformation information</param>
        public ContentTransformator(ClientSidePage page, PageTransformation pageTransformation)
        {
            this.page = page ?? throw new ArgumentException("Page cannot be null");
            this.pageTransformation = pageTransformation ?? throw new ArgumentException("pageTransformation cannot be null");
            this.functionProcessor = new FunctionProcessor(this.page, this.pageTransformation);
            this.siteTokens = CreateSiteTokenList(page.Context);
        }
        #endregion

        /// <summary>
        /// Transforms the passed web parts into the loaded client side page
        /// </summary>
        /// <param name="webParts">List of web parts that need to be transformed</param>
        public void Transform(List<WebPartEntity> webParts)
        {
            if (webParts == null || webParts.Count == 0)
            {
                // nothing to transform
                return;
            }

            // find the default mapping, will be used for webparts for which the model does not contain a mapping
            var defaultMapping = pageTransformation.BaseWebPart.Mappings.Mapping.Where(p => p.Default == true).FirstOrDefault();
            if (defaultMapping == null)
            {
                throw new Exception("No default mapping was found int the provided mapping file");
            }

            // Load existing available controls
            var componentsToAdd = page.AvailableClientSideComponents().ToList();

            // Iterate over the web parts, important to order them by row, column and zoneindex
            foreach (var webPart in webParts.OrderBy(p => p.Row).OrderBy(p => p.Column).OrderBy(p =>p.Order))
            {
                // Title bar will never be migrated
                if (webPart.Type == WebParts.TitleBar)
                {
                    continue;
                }

                // Add site level (e.g. site) tokens to the web part properties so they can be used in the same manner as a web part property
                foreach (var token in this.siteTokens)
                {
                    webPart.Properties.Add(token.Key, token.Value);
                }

                Mapping mapping = defaultMapping;
                // Does the web part have a mapping defined?
                var webPartData = pageTransformation.WebParts.Where(p => p.Type == webPart.Type).FirstOrDefault();
                if (webPartData != null && webPartData.Mappings != null)
                {
                    // The mapping can have a selector function defined, is so it will be executed. If a selector was executed the selectorResult will contain the name of the mapping to use
                    var selectorResult = functionProcessor.Process(ref webPartData, webPart);

                    Mapping webPartMapping = null;
                    // Get the needed mapping:
                    // - use the mapping returned by the selector
                    // - if no selector then take the default mapping
                    // - if no mapping found we'll fall back to the default web part mapping
                    if (!string.IsNullOrEmpty(selectorResult))
                    {
                        webPartMapping = webPartData.Mappings.Mapping.Where(p => p.Name.Equals(selectorResult, StringComparison.InvariantCultureIgnoreCase)).FirstOrDefault();
                    }
                    else
                    {
                        webPartMapping = webPartData.Mappings.Mapping.Where(p => p.Default == true).FirstOrDefault();
                    }

                    if (webPartMapping != null)
                    {
                        mapping = webPartMapping;
                    }
                }

                // Use the mapping data => make one list of Text and WebParts to allow for correct ordering
                combinedMappinglist = new List<CombinedMapping>();
                if (mapping.ClientSideText != null)
                {
                    foreach (var map in mapping.ClientSideText.OrderBy(p => p.Order))
                    {
                        if (!Int32.TryParse(map.Order, out Int32 mapOrder))
                        {
                            mapOrder = 0;
                        }

                        combinedMappinglist.Add(new CombinedMapping { ClientSideText = map, ClientSideWebPart = null, Order = mapOrder });
                    }
                }
                if (mapping.ClientSideWebPart != null)
                {
                    foreach (var map in mapping.ClientSideWebPart.OrderBy(p => p.Order))
                    {
                        if (!Int32.TryParse(map.Order, out Int32 mapOrder))
                        {
                            mapOrder = 0;
                        }

                        combinedMappinglist.Add(new CombinedMapping { ClientSideText = null, ClientSideWebPart = map, Order = mapOrder });
                    }
                }

                // Get the order of the last inserted control in this column
                int order = LastColumnOrder(webPart.Row - 1, webPart.Column - 1);
                // Interate the controls for this mapping using their order
                foreach (var map in combinedMappinglist.OrderBy(p => p.Order))
                {
                    order++;

                    if (map.ClientSideText != null)
                    {
                        // Insert a Text control
                        OfficeDevPnP.Core.Pages.ClientSideText text = new OfficeDevPnP.Core.Pages.ClientSideText()
                        {
                            Text = TokenParser.ReplaceTokens(map.ClientSideText.Text, webPart)
                        };

                        page.AddControl(text, page.Sections[webPart.Row - 1].Columns[webPart.Column - 1], order);
                    }
                    else if (map.ClientSideWebPart != null)
                    {
                        // Insert a web part
                        ClientSideComponent baseControl = null;

                        if (map.ClientSideWebPart.Type == ClientSideWebPartType.Custom)
                        {
                            baseControl = componentsToAdd.FirstOrDefault(p => p.Id.Equals(map.ClientSideWebPart.ControlId, StringComparison.InvariantCultureIgnoreCase));
                        }
                        else
                        {
                            string webPartName = "";
                            switch (map.ClientSideWebPart.Type)
                            {
                                case ClientSideWebPartType.List:
                                    {
                                        webPartName = ClientSidePage.ClientSideWebPartEnumToName(DefaultClientSideWebParts.List);
                                        break;
                                    }
                                case ClientSideWebPartType.Image:
                                    {
                                        webPartName = ClientSidePage.ClientSideWebPartEnumToName(DefaultClientSideWebParts.Image);
                                        break;
                                    }
                                case ClientSideWebPartType.ContentRollup:
                                    {
                                        webPartName = ClientSidePage.ClientSideWebPartEnumToName(DefaultClientSideWebParts.ContentRollup);
                                        break;
                                    }
                                case ClientSideWebPartType.BingMap:
                                    {
                                        webPartName = ClientSidePage.ClientSideWebPartEnumToName(DefaultClientSideWebParts.BingMap);
                                        break;
                                    }
                                case ClientSideWebPartType.ContentEmbed:
                                    {
                                        webPartName = ClientSidePage.ClientSideWebPartEnumToName(DefaultClientSideWebParts.ContentEmbed);
                                        break;
                                    }
                                case ClientSideWebPartType.DocumentEmbed:
                                    {
                                        webPartName = ClientSidePage.ClientSideWebPartEnumToName(DefaultClientSideWebParts.DocumentEmbed);
                                        break;
                                    }
                                case ClientSideWebPartType.ImageGallery:
                                    {
                                        webPartName = ClientSidePage.ClientSideWebPartEnumToName(DefaultClientSideWebParts.ImageGallery);
                                        break;
                                    }
                                case ClientSideWebPartType.LinkPreview:
                                    {
                                        webPartName = ClientSidePage.ClientSideWebPartEnumToName(DefaultClientSideWebParts.LinkPreview);
                                        break;
                                    }
                                case ClientSideWebPartType.NewsFeed:
                                    {
                                        webPartName = ClientSidePage.ClientSideWebPartEnumToName(DefaultClientSideWebParts.NewsFeed);
                                        break;
                                    }
                                case ClientSideWebPartType.NewsReel:
                                    {
                                        webPartName = ClientSidePage.ClientSideWebPartEnumToName(DefaultClientSideWebParts.NewsReel);
                                        break;
                                    }
                                case ClientSideWebPartType.PowerBIReportEmbed:
                                    {
                                        webPartName = ClientSidePage.ClientSideWebPartEnumToName(DefaultClientSideWebParts.PowerBIReportEmbed);
                                        break;
                                    }
                                case ClientSideWebPartType.QuickChart:
                                    {
                                        webPartName = ClientSidePage.ClientSideWebPartEnumToName(DefaultClientSideWebParts.QuickChart);
                                        break;
                                    }
                                case ClientSideWebPartType.SiteActivity:
                                    {
                                        webPartName = ClientSidePage.ClientSideWebPartEnumToName(DefaultClientSideWebParts.SiteActivity);
                                        break;
                                    }
                                case ClientSideWebPartType.VideoEmbed:
                                    {
                                        webPartName = ClientSidePage.ClientSideWebPartEnumToName(DefaultClientSideWebParts.VideoEmbed);
                                        break;
                                    }
                                case ClientSideWebPartType.YammerEmbed:
                                    {
                                        webPartName = ClientSidePage.ClientSideWebPartEnumToName(DefaultClientSideWebParts.YammerEmbed);
                                        break;
                                    }
                                case ClientSideWebPartType.Events:
                                    {
                                        webPartName = ClientSidePage.ClientSideWebPartEnumToName(DefaultClientSideWebParts.Events);
                                        break;
                                    }
                                case ClientSideWebPartType.GroupCalendar:
                                    {
                                        webPartName = ClientSidePage.ClientSideWebPartEnumToName(DefaultClientSideWebParts.GroupCalendar);
                                        break;
                                    }
                                case ClientSideWebPartType.Hero:
                                    {
                                        webPartName = ClientSidePage.ClientSideWebPartEnumToName(DefaultClientSideWebParts.Hero);
                                        break;
                                    }
                                case ClientSideWebPartType.PageTitle:
                                    {
                                        webPartName = ClientSidePage.ClientSideWebPartEnumToName(DefaultClientSideWebParts.PageTitle);
                                        break;
                                    }
                                case ClientSideWebPartType.People:
                                    {
                                        webPartName = ClientSidePage.ClientSideWebPartEnumToName(DefaultClientSideWebParts.People);
                                        break;
                                    }
                                case ClientSideWebPartType.QuickLinks:
                                    {
                                        webPartName = ClientSidePage.ClientSideWebPartEnumToName(DefaultClientSideWebParts.QuickLinks);
                                        break;
                                    }
                                case ClientSideWebPartType.Divider:
                                    {
                                        webPartName = ClientSidePage.ClientSideWebPartEnumToName(DefaultClientSideWebParts.Divider);
                                        break;
                                    }
                                case ClientSideWebPartType.MicrosoftForms:
                                    {
                                        webPartName = ClientSidePage.ClientSideWebPartEnumToName(DefaultClientSideWebParts.MicrosoftForms);
                                        break;
                                    }
                                case ClientSideWebPartType.Spacer:
                                    {
                                        webPartName = ClientSidePage.ClientSideWebPartEnumToName(DefaultClientSideWebParts.Spacer);
                                        break;
                                    }
                                default:
                                    {
                                        break;
                                    }
                            }

                            baseControl = componentsToAdd.FirstOrDefault(p => p.Name.Equals(webPartName, StringComparison.InvariantCultureIgnoreCase));
                        }

                        // If we found the web part as a possible candidate to use then add it
                        if (baseControl != null)
                        {
                            var jsonDecoded = WebUtility.HtmlDecode(TokenParser.ReplaceTokens(map.ClientSideWebPart.JsonControlData, webPart));
                            OfficeDevPnP.Core.Pages.ClientSideWebPart myWebPart = new OfficeDevPnP.Core.Pages.ClientSideWebPart(baseControl)
                            {
                                Order = map.Order,
                                PropertiesJson = jsonDecoded
                            };

                            page.AddControl(myWebPart, page.Sections[webPart.Row - 1].Columns[webPart.Column - 1], order);
                        }
                        else
                        {
                            //Log warning: web part was not found
                        }

                    }
                }
            }
        }

        #region Helper methods
        private Dictionary<string, string> CreateSiteTokenList(ClientContext cc)
        {
            Dictionary<string, string> siteTokens = new Dictionary<string, string>(5);

            cc.Web.EnsureProperties(p => p.Url, p => p.ServerRelativeUrl, p => p.Id);
            cc.Site.EnsureProperties(p => p.RootWeb.ServerRelativeUrl, p => p.Id);

            siteTokens.Add("Web", cc.Web.ServerRelativeUrl.TrimEnd('/'));
            siteTokens.Add("Sitecollection", cc.Site.RootWeb.ServerRelativeUrl.TrimEnd('/'));
            siteTokens.Add("WebId", cc.Web.Id.ToString());
            siteTokens.Add("SiteId", cc.Site.Id.ToString());

            return siteTokens;
        }

        private Int32 LastColumnOrder(int row, int col)
        {
            var lastControl = page.Sections[row].Columns[col].Controls.OrderBy(p => p.Order).LastOrDefault();
            if (lastControl != null)
            {
                return lastControl.Order;
            }
            else
            {
                return -1;
            }
        }
        #endregion

    }
}
