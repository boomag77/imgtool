using ImgViewer.Views;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ImgViewer.Models
{
    internal class Pipeline
    {

        private ObservableCollection<PipeLineOperation> _pipeLineOperations = new();

        public ObservableCollection<PipeLineOperation> PipeLineOperations => _pipeLineOperations;

    }
}
