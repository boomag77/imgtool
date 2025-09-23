using ImgProcessor.Abstractions;
using LeadImgProcessor;
using OpenCVProcessor;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ImgViewer.Models
{
    internal class ImageProcessorFactory : IImageProcessorFactory
    {
        public ImageProcessorFactory()
        { }

        public IImageProcessor CreateProcessor(ImageProcessorType ptocType, string licPath = null, string licKey = null)
        {
            switch (ptocType)
            {
                case ImageProcessorType.OpenCV:
                    return new OpenCVImageProcessor();

                case ImageProcessorType.ImageMagick:
                    throw new NotImplementedException("ImageMagick processor is not implemented in this factory.");
                case ImageProcessorType.Leadtools:
                    return new LeadImageProcessor();
                default:
                    throw new ArgumentException("Unsupported processor type.");
            }
        }


    }
}
