﻿namespace AppmetrS2S.Persister
{
    #region using directives

    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.IO.Compression;
    using System.Linq;
    using System.Threading;
    using Actions;
    using log4net;

    #endregion

    public class FileBatchPersister : IBatchPersister
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(FileBatchPersister));

        private readonly ReaderWriterLock _lock = new ReaderWriterLock();

        private const String BatchFilePrefix = "batchFile#";

        private readonly String _filePath;
        private readonly String _batchIdFile;

        private Queue<int> _fileIds;
        private int _lastBatchId;

        public FileBatchPersister(String filePath)
        {
            if (!Directory.Exists(filePath))
            {
                Directory.CreateDirectory(filePath);
            }

            _filePath = filePath;
            _batchIdFile = Path.Combine(Path.GetFullPath(_filePath), "lastBatchId");

            InitPersistedFiles();
        }

        public Batch GetNext()
        {
            _lock.AcquireReaderLock(-1);
            try
            {
                if (_fileIds.Count == 0) return null;

                int batchId = _fileIds.Peek();
                string batchFilePath = Path.Combine(_filePath, GetBatchFileName(batchId));

                if (File.Exists(batchFilePath))
                {
                    using (var fileStream = new FileStream(batchFilePath, FileMode.Open))
                    using (var deflateStream = new DeflateStream(fileStream, CompressionMode.Decompress))
                    {
                        Batch batch;
                        if (Utils.TryReadBatch(deflateStream, out batch))
                        {
                            return batch;
                        }
                    }

                    if (Log.IsErrorEnabled)
                    {
                        Log.ErrorFormat("Error while reading batch for id {0}", batchId);
                    }
                }

                return null;
            }
            finally
            {
                _lock.ReleaseReaderLock();
            }
        }

        public void Persist(List<AppMetrAction> actions)
        {
            _lock.AcquireWriterLock(-1);

            string batchFilePath = Path.Combine(_filePath, GetBatchFileName(_lastBatchId));
            try
            {
                using (var fileStream = new FileStream(batchFilePath, FileMode.CreateNew))
                using (var deflateStream = new DeflateStream(fileStream, CompressionLevel.Optimal))
                {
                    if (Log.IsDebugEnabled)
                    {
                        Log.DebugFormat("Persis batch {0}", _lastBatchId);
                    }
                    Utils.WriteBatch(deflateStream, new Batch(_lastBatchId, actions));
                    _fileIds.Enqueue(_lastBatchId);

                    UpdateLastBatchId();
                }
            }
            catch (Exception e)
            {
                if (Log.IsErrorEnabled)
                {
                    Log.Error("Error in batch persist", e);
                }

                if (File.Exists(batchFilePath))
                {
                    File.Delete(batchFilePath);
                }
            }
            finally
            {
                _lock.ReleaseWriterLock();
            }
        }

        public void Remove()
        {
            _lock.AcquireWriterLock(-1);

            try
            {
                if (Log.IsDebugEnabled)
                {
                    Log.DebugFormat("Remove file with index {0}", _fileIds.Peek());
                }

                File.Delete(Path.Combine(_filePath, GetBatchFileName(_fileIds.Dequeue())));
            }
            finally
            {
                _lock.ReleaseWriterLock();
            }
        }

        private void InitPersistedFiles()
        {
            String[] files = Directory.GetFiles(_filePath, String.Format("{0}*", BatchFilePrefix));

            var ids =
                files.Select(file => Convert.ToInt32(Path.GetFileName(file).Substring(BatchFilePrefix.Length))).ToList();
            ids.Sort();

            String batchId;
            if (File.Exists(_batchIdFile) && (batchId = File.ReadAllText(_batchIdFile)).Length > 0)
            {
                _lastBatchId = Convert.ToInt32(batchId);
            }
            else if (ids.Count > 0)
            {
                _lastBatchId = ids[ids.Count - 1];
            }
            else
            {
                _lastBatchId = 0;
            }

            Log.InfoFormat("Init lastBatchId with {0}", _lastBatchId);

            if (Log.IsInfoEnabled)
            {
                Log.InfoFormat("Load {0} files from disk", ids.Count);
                if (ids.Count > 0)
                {
                    Log.InfoFormat("First batch id is {0}, last is {1}", ids[0], ids[ids.Count - 1]);
                }
            }

            _fileIds = new Queue<int>(ids);
        }

        private void UpdateLastBatchId()
        {
            _lastBatchId++;
            File.WriteAllText(_batchIdFile, Convert.ToString(_lastBatchId));
        }

        private String GetBatchFileName(int batchId)
        {
            return String.Format("{0}{1:D11}", BatchFilePrefix, batchId);
        }
    }
}