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

using System;
using System.Threading.Tasks;
using CoCoL;
using Duplicati.Library.Main.Volumes;
using Duplicati.Library.Utility;

namespace Duplicati.Library.Main.Operation.Restore
{
    internal class VolumeDecompressor
    {
        private static readonly string LOGTAG = Logging.Log.LogTagFromType<VolumeDecompressor>();

        public static Task Run(Options options, RestoreResults results)
        {
            return AutomationExtensions.RunTask(
            new
            {
                Input = Channels.decryptedVolume.ForRead,
                Output = Channels.decompressedVolumes.ForWrite
            },
            async self =>
            {
                try {
                    using var block_hasher = HashFactory.CreateHasher(options.BlockHashAlgorithm);

                    while (true)
                    {
                        var (block_request, volume) = await self.Input.ReadAsync();

                        byte[] buffer = new byte[block_request.BlockSize];
                        new BlockVolumeReader(options.CompressionModule, volume, options).ReadBlock(block_request.BlockHash, buffer);
                        var hash = Convert.ToBase64String(block_hasher.ComputeHash(buffer, 0, (int)block_request.BlockSize));

                        if (hash != block_request.BlockHash)
                        {
                            Logging.Log.WriteWarningMessage(LOGTAG, "InvalidBlock", null, $"Invalid block detected for block {block_request.BlockID} in volume {block_request.VolumeID}, expected hash: {block_request.BlockHash}, actual hash: {hash}");
                            lock (results)
                            {
                                results.BrokenRemoteFiles.Add(block_request.VolumeID);
                            }
                        }

                        await self.Output.WriteAsync((block_request, buffer));
                    }
                }
                catch (RetiredException)
                {
                    Logging.Log.WriteVerboseMessage(LOGTAG, "RetiredProcess", null, "Volume decompressor retired");
                }
                catch (Exception ex)
                {
                    Logging.Log.WriteErrorMessage(LOGTAG, "DecompressionError", ex, "Error during decompression");
                    throw;
                }
            });
        }
    }
}