using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ImgViewer.Interfaces
{
    internal interface IBatchDispatcher
    {

        public enum BatchTaskPriority
        {
            Low = 0,
            Medium = 1,
            High = 2
        }

        public enum BatchTaskStatus
        {
            Pending,
            InProgress,
            Paused,
            Completed,
            Failed
        }

        public sealed class BatchTask
        {
            public string SourceFolder { get; set; }
            public string DestinationFolder { get; set; }
            public Pipeline Pipeline { get; set; }
            public uint StartTimeUnix { get; set; }

            public uint TaskId { get; set; }
            public int ProgressPercentage { get; set; }
            public BatchTaskPriority Priority { get; set; }
            public BatchTaskStatus Status { get; set; }

        }

        ConcurrentDictionary<uint, BatchTask> TasksQueue { get; set; }

        void EnqueueTask(BatchTask task);

        void DequeueTask(BatchTask task);
        
        void ClearTasks();

        IEnumerable<BatchTask> GetAllTasks();

        BatchTask? GetTaskById(uint taskId);

        void UpdateTaskStatus(uint taskId, BatchTaskStatus status, int progressPercentage);


    }
}
