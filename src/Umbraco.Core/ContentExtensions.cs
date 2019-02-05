﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Web;
using System.Xml.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Umbraco.Core.Composing;
using Umbraco.Core.IO;
using Umbraco.Core.Models;
using Umbraco.Core.Models.Entities;
using Umbraco.Core.Models.Membership;
using Umbraco.Core.Services;
using Umbraco.Core.Services.Implement;

namespace Umbraco.Core
{
    public static class ContentExtensions
    {
        // this ain't pretty
        private static IMediaFileSystem _mediaFileSystem;
        private static IMediaFileSystem MediaFileSystem => _mediaFileSystem ?? (_mediaFileSystem = Current.MediaFileSystem);

        #region IContent

        /// <summary>
        /// Gets the current status of the Content
        /// </summary>
        public static ContentStatus GetStatus(this IContent content, string culture = null)
        {
            if (content.Trashed)
                return ContentStatus.Trashed;

            if (!content.ContentType.VariesByCulture())
                culture = string.Empty;
            else if (culture.IsNullOrWhiteSpace())
                throw new ArgumentNullException($"{nameof(culture)} cannot be null or empty");

            var expires = content.ContentSchedule.GetSchedule(culture, ContentScheduleAction.Expire);
            if (expires != null && expires.Any(x => x.Date > DateTime.MinValue && DateTime.Now > x.Date))
                return ContentStatus.Expired;

            var release = content.ContentSchedule.GetSchedule(culture, ContentScheduleAction.Release);
            if (release != null && release.Any(x => x.Date > DateTime.MinValue && x.Date > DateTime.Now))
                return ContentStatus.AwaitingRelease;

            if (content.Published)
                return ContentStatus.Published;

            return ContentStatus.Unpublished;
        }

        /// <summary>
        /// Gets the cultures that have been flagged for unpublishing.
        /// </summary>
        /// <remarks>Gets cultures for which content.UnpublishCulture() has been invoked.</remarks>
        internal static IReadOnlyList<string> GetCulturesUnpublishing(this IContent content, IContent persisted)
        {
            //TODO: The reason we need a ref to the original persisted IContent is to check if it is published
            // however, for performance reasons we could pass in a ContentCultureInfosCollection which could be
            // resolved from the database much more quickly than resolving an entire IContent object.
            // That said, the GetById on the IContentService will return from cache so might not be something to worry about.


            if (!content.Published || !content.ContentType.VariesByCulture() || !content.IsPropertyDirty("PublishCultureInfos"))
                return Array.Empty<string>();

            var culturesChanging = content.CultureInfos.Values.Where(x => x.IsDirty()).Select(x => x.Culture);
            return culturesChanging
                .Where(x => !content.IsCulturePublished(x) && // is not published anymore
                            persisted != null && persisted.IsCulturePublished(x))   // but was published before
                .ToList();
        }
        
        #endregion

        /// <summary>
        /// Removes characters that are not valid XML characters from all entity properties
        /// of type string. See: http://stackoverflow.com/a/961504/5018
        /// </summary>
        /// <returns></returns>
        /// <remarks>
        /// If this is not done then the xml cache can get corrupt and it will throw YSODs upon reading it.
        /// </remarks>
        /// <param name="entity"></param>
        public static void SanitizeEntityPropertiesForXmlStorage(this IContentBase entity)
        {
            entity.Name = entity.Name.ToValidXmlString();
            foreach (var property in entity.Properties)
            {
                foreach (var propertyValue in property.Values)
                {
                    if (propertyValue.EditedValue is string editString)
                        propertyValue.EditedValue = editString.ToValidXmlString();
                    if (propertyValue.PublishedValue is string publishedString)
                        propertyValue.PublishedValue = publishedString.ToValidXmlString();
                }
            }
        }

        /// <summary>
        /// Checks if the IContentBase has children
        /// </summary>
        /// <param name="content"></param>
        /// <param name="services"></param>
        /// <returns></returns>
        /// <remarks>
        /// This is a bit of a hack because we need to type check!
        /// </remarks>
        internal static bool HasChildren(IContentBase content, ServiceContext services)
        {
            if (content is IContent)
            {
                return services.ContentService.HasChildren(content.Id);
            }
            if (content is IMedia)
            {
                return services.MediaService.HasChildren(content.Id);
            }
            return false;
        }

        /// <summary>
        /// Returns properties that do not belong to a group
        /// </summary>
        /// <param name="content"></param>
        /// <returns></returns>
        public static IEnumerable<Property> GetNonGroupedProperties(this IContentBase content)
        {
            var propertyIdsInTabs = content.PropertyGroups.SelectMany(pg => pg.PropertyTypes);
            return content.Properties
                          .Where(property => propertyIdsInTabs.Contains(property.PropertyType) == false)
                          .OrderBy(x => x.PropertyType.SortOrder);
        }

        /// <summary>
        /// Returns the Property object for the given property group
        /// </summary>
        /// <param name="content"></param>
        /// <param name="propertyGroup"></param>
        /// <returns></returns>
        public static IEnumerable<Property> GetPropertiesForGroup(this IContentBase content, PropertyGroup propertyGroup)
        {
            //get the properties for the current tab
            return content.Properties
                          .Where(property => propertyGroup.PropertyTypes
                                                          .Select(propertyType => propertyType.Id)
                                                          .Contains(property.PropertyTypeId));
        }

        #region SetValue for setting file contents

        /// <summary>
        /// Sets the posted file value of a property.
        /// </summary>
        /// <remarks>This really is for FileUpload fields only, and should be obsoleted. For anything else,
        /// you need to store the file by yourself using Store and then figure out
        /// how to deal with auto-fill properties (if any) and thumbnails (if any) by yourself.</remarks>
        public static void SetValue(this IContentBase content, IContentTypeBaseServiceProvider contentTypeBaseServiceProvider, string propertyTypeAlias, string filename, Stream filestream, string culture = null, string segment = null)
        {
            if (filename == null || filestream == null) return;

            // get a safe & clean filename
            filename = IOHelper.SafeFileName(filename);
            if (string.IsNullOrWhiteSpace(filename)) return;
            filename = filename.ToLower();

            SetUploadFile(content,contentTypeBaseServiceProvider, propertyTypeAlias, filename, filestream, culture, segment);
        }

        private static void SetUploadFile(this IContentBase content, IContentTypeBaseServiceProvider contentTypeBaseServiceProvider, string propertyTypeAlias, string filename, Stream filestream, string culture = null, string segment = null)
        {
            var property = GetProperty(content, contentTypeBaseServiceProvider, propertyTypeAlias);

            // Fixes https://github.com/umbraco/Umbraco-CMS/issues/3937 - Assigning a new file to an
            // existing IMedia with extension SetValue causes exception 'Illegal characters in path'
            string oldpath = null;
            if (property.GetValue(culture, segment) is string svalue)
            {
                if (svalue.DetectIsJson())
                {
                    // the property value is a JSON serialized image crop data set - grab the "src" property as the file source
                    var jObject = JsonConvert.DeserializeObject<JObject>(svalue);
                    svalue = jObject != null ? jObject.GetValueAsString("src") : svalue;
                }
                oldpath = MediaFileSystem.GetRelativePath(svalue);
            }

            var filepath = MediaFileSystem.StoreFile(content, property.PropertyType, filename, filestream, oldpath);
            property.SetValue(MediaFileSystem.GetUrl(filepath), culture, segment);
        }

        // gets or creates a property for a content item.
        private static Property GetProperty(IContentBase content, IContentTypeBaseServiceProvider contentTypeBaseServiceProvider, string propertyTypeAlias)
        {
            var property = content.Properties.FirstOrDefault(x => x.Alias.InvariantEquals(propertyTypeAlias));
            if (property != null) return property;

            var contentTypeService = contentTypeBaseServiceProvider.For(content);
            var contentType = contentTypeService.Get(content.ContentTypeId);
            var propertyType = contentType.CompositionPropertyTypes
                .FirstOrDefault(x => x.Alias.InvariantEquals(propertyTypeAlias));
            if (propertyType == null)
                throw new Exception("No property type exists with alias " + propertyTypeAlias + ".");

            property = new Property(propertyType);
            content.Properties.Add(property);
            return property;
        }

        /// <summary>
        /// Stores a file.
        /// </summary>
        /// <param name="content"><see cref="IContentBase"/>A content item.</param>
        /// <param name="propertyTypeAlias">The property alias.</param>
        /// <param name="filename">The name of the file.</param>
        /// <param name="filestream">A stream containing the file data.</param>
        /// <param name="filepath">The original file path, if any.</param>
        /// <returns>The path to the file, relative to the media filesystem.</returns>
        /// <remarks>
        /// <para>Does NOT set the property value, so one should probably store the file and then do
        /// something alike: property.Value = MediaHelper.FileSystem.GetUrl(filepath).</para>
        /// <para>The original file path is used, in the old media file path scheme, to try and reuse
        /// the "folder number" that was assigned to the previous file referenced by the property,
        /// if any.</para>
        /// </remarks>
        public static string StoreFile(this IContentBase content, IContentTypeBaseServiceProvider contentTypeBaseServiceProvider, string propertyTypeAlias, string filename, Stream filestream, string filepath)
        {
            var contentTypeService = contentTypeBaseServiceProvider.For(content);
            var contentType = contentTypeService.Get(content.ContentTypeId);
            var propertyType = contentType
                .CompositionPropertyTypes.FirstOrDefault(x => x.Alias.InvariantEquals(propertyTypeAlias));
            if (propertyType == null) throw new ArgumentException("Invalid property type alias " + propertyTypeAlias + ".");
            return MediaFileSystem.StoreFile(content, propertyType, filename, filestream, filepath);
        }

        #endregion

        #region User/Profile methods

        /// <summary>
        /// Gets the <see cref="IProfile"/> for the Creator of this media item.
        /// </summary>
        public static IProfile GetCreatorProfile(this IMedia media, IUserService userService)
        {
            return userService.GetProfileById(media.CreatorId);
        }

        [Obsolete("Use the overload that declares the IUserService to use")]
        [EditorBrowsable(EditorBrowsableState.Never)]
        public static IProfile GetCreatorProfile(this IContentBase content)
        {
            return Current.Services.UserService.GetProfileById(content.CreatorId);
        }

        /// <summary>
        /// Gets the <see cref="IProfile"/> for the Creator of this content item.
        /// </summary>
        public static IProfile GetCreatorProfile(this IContentBase content, IUserService userService)
        {
            return userService.GetProfileById(content.CreatorId);
        }

        [Obsolete("Use the overload that declares the IUserService to use")]
        [EditorBrowsable(EditorBrowsableState.Never)]
        public static IProfile GetWriterProfile(this IContent content)
        {
            return Current.Services.UserService.GetProfileById(content.WriterId);
        }

        /// <summary>
        /// Gets the <see cref="IProfile"/> for the Writer of this content.
        /// </summary>
        public static IProfile GetWriterProfile(this IContent content, IUserService userService)
        {
            return userService.GetProfileById(content.WriterId);
        }

        /// <summary>
        /// Gets the <see cref="IProfile"/> for the Writer of this content.
        /// </summary>
        public static IProfile GetWriterProfile(this IMedia content, IUserService userService)
        {
            return userService.GetProfileById(content.WriterId);
        }

        #endregion

        #region XML methods

        /// <summary>
        /// Creates the full xml representation for the <see cref="IContent"/> object and all of it's descendants
        /// </summary>
        /// <param name="content"><see cref="IContent"/> to generate xml for</param>
        /// <param name="serializer"></param>
        /// <returns>Xml representation of the passed in <see cref="IContent"/></returns>
        internal static XElement ToDeepXml(this IContent content, IEntityXmlSerializer serializer)
        {
            return serializer.Serialize(content, false, true);
        }

        /// <summary>
        /// Creates the xml representation for the <see cref="IContent"/> object
        /// </summary>
        /// <param name="content"><see cref="IContent"/> to generate xml for</param>
        /// <param name="serializer"></param>
        /// <returns>Xml representation of the passed in <see cref="IContent"/></returns>
        public static XElement ToXml(this IContent content, IEntityXmlSerializer serializer)
        {
            return serializer.Serialize(content, false, false);
        }


        /// <summary>
        /// Creates the xml representation for the <see cref="IMedia"/> object
        /// </summary>
        /// <param name="media"><see cref="IContent"/> to generate xml for</param>
        /// <param name="serializer"></param>
        /// <returns>Xml representation of the passed in <see cref="IContent"/></returns>
        public static XElement ToXml(this IMedia media, IEntityXmlSerializer serializer)
        {
            return serializer.Serialize(media);
        }

        /// <summary>
        /// Creates the xml representation for the <see cref="IMember"/> object
        /// </summary>
        /// <param name="member"><see cref="IMember"/> to generate xml for</param>
        /// <param name="serializer"></param>
        /// <returns>Xml representation of the passed in <see cref="IContent"/></returns>
        public static XElement ToXml(this IMember member, IEntityXmlSerializer serializer)
        {
            return serializer.Serialize(member);
        }
        #endregion

        #region Dirty

        public static IEnumerable<string> GetDirtyUserProperties(this IContentBase entity)
        {
            return entity.Properties.Where(x => x.IsDirty()).Select(x => x.Alias);
        }

        public static bool IsAnyUserPropertyDirty(this IContentBase entity)
        {
            return entity.Properties.Any(x => x.IsDirty());
        }

        public static bool WasAnyUserPropertyDirty(this IContentBase entity)
        {
            return entity.Properties.Any(x => x.WasDirty());
        }

        #endregion
    }
}
