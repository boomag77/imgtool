using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media;

namespace ImgViewer.Interfaces
{
    public interface IImageConverter
    {
        ImageSource ConvertToImageSource(byte[] imageBytes);
    }
}
