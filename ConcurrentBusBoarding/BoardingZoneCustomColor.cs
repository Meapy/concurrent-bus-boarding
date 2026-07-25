using Colossal.Serialization.Entities;
using Unity.Entities;
using Unity.Mathematics;
using UnityColor = UnityEngine.Color;

namespace ConcurrentBusBoarding
{
    public struct BoardingZoneCustomColor : IComponentData, ISerializable
    {
        public int m_Rgb;

        internal BoardingZoneCustomColor(int rgb)
        {
            m_Rgb = rgb & 0xffffff;
        }

        internal UnityColor ToColor(float alpha)
        {
            return new UnityColor(
                ((m_Rgb >> 16) & 0xff) / 255f,
                ((m_Rgb >> 8) & 0xff) / 255f,
                (m_Rgb & 0xff) / 255f,
                math.saturate(alpha));
        }

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(m_Rgb);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out m_Rgb);
            m_Rgb &= 0xffffff;
        }
    }
}
