// Copyright (C) 2024, The Duplicati Team
// https://duplicati.com, hello@duplicati.com
// 
// Permission is hereby granted, free of charge, to any person obtaining a 
// copy of this software and associated documentation files (the "Software"), 
// to deal in the Software without restriction, including without limitation 
// the rights to use, copy, modify, merge, publish, distribute, sublicense, 
// and/or sell copies of the Software, and to permit persons to whom the 
// Software is furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in 
// all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS 
// OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, 
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE 
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER 
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING 
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER 
// DEALINGS IN THE SOFTWARE.


using Amazon.S3;
using Amazon.S3.Model;
using Duplicati.Library.Interface;
using Duplicati.Library.Utility;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Duplicati.Library.Backend
{
    /// <summary>
    /// Helper class that fixes long list support and injects location headers, includes using directives etc.
    /// </summary>
    public class S3AwsClient : IS3Client
    {
        private static readonly string LOGTAG = Logging.Log.LogTagFromType<S3AwsClient>();
        private const int ITEM_LIST_LIMIT = 1000;

        private readonly string m_locationConstraint;
        private readonly string m_storageClass;
        private AmazonS3Client m_client;
        private readonly bool m_useChunkEncoding;

        private readonly string m_dnsHost;

        public S3AwsClient(string awsID, string awsKey, string locationConstraint, string servername,
            string storageClass, bool useSSL, bool disableChunkEncoding, Dictionary<string, string> options)
        {
            var cfg = S3AwsClient.GetDefaultAmazonS3Config();
            cfg.UseHttp = !useSSL;
            cfg.ServiceURL = (useSSL ? "https://" : "http://") + servername;

            foreach (var opt in options.Keys.Where(x => x.StartsWith("s3-ext-", StringComparison.OrdinalIgnoreCase)))
            {
                var prop = cfg.GetType().GetProperties().FirstOrDefault(x =>
                    string.Equals(x.Name, opt.Substring("s3-ext-".Length), StringComparison.OrdinalIgnoreCase));
                if (prop != null && prop.CanWrite)
                {
                    if (prop.PropertyType == typeof(bool))
                        prop.SetValue(cfg, Utility.Utility.ParseBoolOption(options, opt));
                    else if (prop.PropertyType.IsEnum)
                        prop.SetValue(cfg, Enum.Parse(prop.PropertyType, options[opt], true));
                    else if (prop.PropertyType == typeof(int))
                        prop.SetValue(cfg, int.Parse(options[opt]));
                    else if (prop.PropertyType == typeof(long))
                        prop.SetValue(cfg, long.Parse(options[opt]));
                    else if (prop.PropertyType == typeof(string))
                        prop.SetValue(cfg, options[opt]);
                }

                if (prop == null)
                    Logging.Log.WriteWarningMessage(LOGTAG, "UnsupportedOption", null, "Unsupported option: {0}", opt);
            }

            m_client = new AmazonS3Client(awsID, awsKey, cfg);

            m_locationConstraint = locationConstraint;
            m_storageClass = storageClass;
            m_dnsHost = string.IsNullOrWhiteSpace(cfg.ServiceURL) ? null : new System.Uri(cfg.ServiceURL).Host;
            m_useChunkEncoding = !disableChunkEncoding;
        }

        public void AddBucket(string bucketName)
        {
            var request = new PutBucketRequest
            {
                BucketName = bucketName,
            };

            if (!string.IsNullOrEmpty(m_locationConstraint))
                request.BucketRegionName = m_locationConstraint;

            m_client.PutBucketAsync(request).GetAwaiter().GetResult();
        }

        internal static AmazonS3Config GetDefaultAmazonS3Config()
        {
            return new AmazonS3Config()
            {
                BufferSize = (int)Utility.Utility.DEFAULT_BUFFER_SIZE,

                // If this is not set, accessing the property will trigger an expensive operation (~30 seconds)
                // to get the region endpoint. The use of ARNs (Amazon Resource Names) doesn't appear to be
                // critical for our usages.
                // See: https://docs.aws.amazon.com/general/latest/gr/aws-arns-and-namespaces.html
                UseArnRegion = false,
            };
        }

        public virtual async Task GetFileStreamAsync(string bucketName, string keyName, System.IO.Stream target, CancellationToken cancelToken)
        {
            var objectGetRequest = new GetObjectRequest
            {
                BucketName = bucketName,
                Key = keyName
            };

            using (var objectGetResponse = await m_client.GetObjectAsync(objectGetRequest).ConfigureAwait(false))
            using (var s = objectGetResponse.ResponseStream)
            {
                try { s.ReadTimeout = (int)TimeSpan.FromMinutes(1).TotalMilliseconds; }
                catch { }

                await Utility.Utility.CopyStreamAsync(s, target, cancelToken).ConfigureAwait(false);
            }
        }

        public string GetDnsHost()
        {
            return m_dnsHost;
        }

        public virtual async Task AddFileStreamAsync(string bucketName, string keyName, System.IO.Stream source,
            CancellationToken cancelToken)
        {
            var objectAddRequest = new PutObjectRequest
            {
                BucketName = bucketName,
                Key = keyName,
                InputStream = source,
                UseChunkEncoding = m_useChunkEncoding
            };
            if (!string.IsNullOrWhiteSpace(m_storageClass))
                objectAddRequest.StorageClass = new S3StorageClass(m_storageClass);

            try
            {
                await m_client.PutObjectAsync(objectAddRequest, cancelToken);
            }
            catch (AmazonS3Exception e)
            {
                //Catch "non-existing" buckets
                if (e.StatusCode == System.Net.HttpStatusCode.NotFound ||
                    "NoSuchBucket".Equals(e.ErrorCode))
                    throw new FolderMissingException(e);

                throw;
            }
        }

        public Task DeleteObjectAsync(string bucketName, string keyName, CancellationToken cancellationToken)
        {
            var objectDeleteRequest = new DeleteObjectRequest
            {
                BucketName = bucketName,
                Key = keyName
            };

            return m_client.DeleteObjectAsync(objectDeleteRequest, cancellationToken);
        }

        public virtual IEnumerable<IFileEntry> ListBucket(string bucketName, string prefix)
        {
            bool isTruncated = true;
            string filename = null;

            //TODO: Figure out if this is the case with AWSSDK too
            //Unfortunately S3 sometimes reports duplicate values when requesting more than one page of results
            //So, track the files that have already been returned and skip any duplicates.
            HashSet<string> alreadyReturned = new HashSet<string>();

            //We truncate after ITEM_LIST_LIMIT elements, and then repeat
            while (isTruncated)
            {
                var listRequest = new ListObjectsRequest { BucketName = bucketName };

                if (!string.IsNullOrEmpty(filename))
                    listRequest.Marker = filename;

                listRequest.MaxKeys = ITEM_LIST_LIMIT;
                if (!string.IsNullOrEmpty(prefix))
                    listRequest.Prefix = prefix;

                ListObjectsResponse listResponse;
                try
                {
                    listResponse = m_client.ListObjectsAsync(listRequest).Await();
                }
                catch (AmazonS3Exception e)
                {
                    if (e.StatusCode == System.Net.HttpStatusCode.NotFound ||
                        "NoSuchBucket".Equals(e.ErrorCode))
                    {
                        throw new FolderMissingException(e);
                    }

                    throw;
                }

                isTruncated = listResponse.IsTruncated;
                filename = listResponse.NextMarker;

                foreach (var obj in listResponse.S3Objects.Where(obj => alreadyReturned.Add(obj.Key)))
                {
                    yield return new Common.IO.FileEntry(
                        obj.Key,
                        obj.Size,
                        obj.LastModified,
                        obj.LastModified
                    );
                }
            }
        }

        public void RenameFile(string bucketName, string source, string target)
        {
            var copyObjectRequest = new CopyObjectRequest
            {
                SourceBucket = bucketName,
                SourceKey = source,
                DestinationBucket = bucketName,
                DestinationKey = target
            };

            m_client.CopyObjectAsync(copyObjectRequest).Await();

            DeleteObjectAsync(bucketName, source, CancellationToken.None).Await();
        }

        #region IDisposable Members

        public void Dispose()
        {
            if (m_client != null)
                m_client.Dispose();
            m_client = null;
        }

        #endregion
    }
}