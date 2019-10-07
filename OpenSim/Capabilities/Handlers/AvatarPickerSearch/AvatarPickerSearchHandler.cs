/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Reflection;
using System.Web;
using log4net;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Capabilities;
using OpenSim.Framework.Servers;
using OpenSim.Framework.Servers.HttpServer;
//using OpenSim.Region.Framework.Interfaces;
using OpenSim.Services.Interfaces;
using Caps = OpenSim.Framework.Capabilities.Caps;

namespace OpenSim.Capabilities.Handlers
{
    public class AvatarPickerSearchHandler : BaseStreamHandler
    {
        private static readonly ILog m_log =
            LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private IPeople m_PeopleService;

        public AvatarPickerSearchHandler(string path, IPeople peopleService, string name, string description)
            : base("GET", path, name, description)
        {
            m_PeopleService = peopleService;
        }

        protected override byte[] ProcessRequest(string path, Stream request, IOSHttpRequest httpRequest, IOSHttpResponse httpResponse)
        {
            // Try to parse the texture ID from the request URL
            NameValueCollection query = HttpUtility.ParseQueryString(httpRequest.Url.Query);
            string names = query.GetOne("names");
            string psize = query.GetOne("page_size");
            string pnumber = query.GetOne("page");

            if (m_PeopleService == null)
                return FailureResponse(names, (int)System.Net.HttpStatusCode.InternalServerError, httpResponse);

            if (string.IsNullOrEmpty(names) || names.Length < 3)
                return FailureResponse(names, (int)System.Net.HttpStatusCode.BadRequest, httpResponse);

            m_log.DebugFormat("[AVATAR PICKER SEARCH]: search for {0}", names);

            int page_size = (string.IsNullOrEmpty(psize) ? 500 : Int32.Parse(psize));
            int page_number = (string.IsNullOrEmpty(pnumber) ? 1 : Int32.Parse(pnumber));

            // Full content request
            httpResponse.StatusCode = (int)System.Net.HttpStatusCode.OK;
            //httpResponse.ContentLength = ??;
            httpResponse.ContentType = "application/llsd+xml";

            List<UserData> users = m_PeopleService.GetUserData(names, page_size, page_number);

            LLSDAvatarPicker osdReply = new LLSDAvatarPicker();
            osdReply.next_page_url = httpRequest.RawUrl;
            foreach (UserData u in users)
                osdReply.agents.Array.Add(ConvertUserData(u));

            string reply = LLSDHelpers.SerialiseLLSDReply(osdReply);
            return System.Text.Encoding.UTF8.GetBytes(reply);
        }

        string getUserName(string firstname, string lastname)
        {
            if (lastname.ToLower() == "resident")
                return firstname.ToLower();
            else return string.Format("{0}.{1}", firstname, lastname).ToLower();
        }

        string getDefaultName(string firstname, string lastname)
        {
            if (lastname.ToLower() == "resident")
                return firstname;
            else return string.Format("{0} {1}", firstname, lastname);
        }

        private LLSDPerson ConvertUserData(UserData user)
        {
            LLSDPerson p = new LLSDPerson();
            p.id = user.Id;
            p.legacy_first_name = user.FirstName;
            p.legacy_last_name = user.LastName;

            bool has_name = !string.IsNullOrWhiteSpace(user.DisplayName);
            
            if(user.LastName.StartsWith("@"))
            {
                string[] split = user.FirstName.Split('.');
                if(split.Length == 2)
                {
                    p.username = getUserName(split[0], split[1]) + "." + user.LastName.ToLower();

                    if(!has_name)
                    {
                        p.display_name = getDefaultName(split[0], split[1]);
                    }
                }

                p.is_display_name_default = false;
            }
            else 
            {
                p.username = getUserName(user.FirstName, user.LastName);
                p.is_display_name_default = !has_name;
                p.display_name = has_name ? user.DisplayName : getDefaultName(user.FirstName, user.LastName);
            }

            return p;
        }

        private byte[] FailureResponse(string names, int statuscode, IOSHttpResponse httpResponse)
        {
            m_log.Error("[AVATAR PICKER SEARCH]: Error searching for " + names);
            httpResponse.StatusCode = (int)System.Net.HttpStatusCode.NotFound;
            return System.Text.Encoding.UTF8.GetBytes(string.Empty);
        }
    }
}