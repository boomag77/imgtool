using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ImgViewer.Internal.Abstractions
{
    public interface JSONProcessor
    {
        T? Deserialize<T>(string json);
        string Serialize<T>(T obj);
    }
}
