using System;
using System.Xml;
using System.Security.Cryptography;
using System.Security.Cryptography.Xml;

namespace MusicBeePlugin
{
    public partial class Plugin
    {
        public static void Encrypt(XmlDocument Doc, string ElementToEncrypt, string EncryptionElementID, RSA Alg, string KeyName)
        {
            if (Doc == null)
                throw new ArgumentNullException("Doc");
            if (ElementToEncrypt == null)
                throw new ArgumentNullException("ElementToEncrypt");
            if (EncryptionElementID == null)
                throw new ArgumentNullException("EncryptionElementID");
            if (Alg == null)
                throw new ArgumentNullException("Alg");
            if (KeyName == null)
                throw new ArgumentNullException("KeyName");

            XmlElement elementToEncrypt = Doc.GetElementsByTagName(ElementToEncrypt)[0] as XmlElement;

            if (elementToEncrypt == null)
            {
                throw new XmlException("The specified element was not found");
            }
            Aes sessionKey = null;

            try
            {
                EncryptedXml eXml = new EncryptedXml();
                EncryptedData edElement = new EncryptedData();
                EncryptedKey ek = new EncryptedKey();
                DataReference dRef = new DataReference();
                KeyInfoName kin = new KeyInfoName();

                sessionKey = Aes.Create();

                byte[] encryptedElement = eXml.EncryptData(elementToEncrypt, sessionKey, false);

                edElement.Type = EncryptedXml.XmlEncElementUrl;
                edElement.Id = EncryptionElementID;
                edElement.EncryptionMethod = new EncryptionMethod(EncryptedXml.XmlEncAES256Url);

                byte[] encryptedKey = EncryptedXml.EncryptKey(sessionKey.Key, Alg, false);

                ek.CipherData = new CipherData(encryptedKey);
                ek.EncryptionMethod = new EncryptionMethod(EncryptedXml.XmlEncRSA15Url);
                dRef.Uri = "#" + EncryptionElementID;
                ek.AddReference(dRef);
                edElement.KeyInfo.AddClause(new KeyInfoEncryptedKey(ek));
                kin.Value = KeyName;
                ek.KeyInfo.AddClause(kin);
                edElement.CipherData.CipherValue = encryptedElement;

                EncryptedXml.ReplaceElement(elementToEncrypt, edElement, false);
            }
            catch (Exception e)
            {
                throw e;
            }
            finally
            {
                if (sessionKey != null)
                {
                    sessionKey.Clear();
                }
            }
        }

        public static void Decrypt(XmlDocument Doc, RSA Alg, string KeyName)
        {
            if (Doc == null)
                throw new ArgumentNullException("Doc");
            if (Alg == null)
                throw new ArgumentNullException("Alg");
            if (KeyName == null)
                throw new ArgumentNullException("KeyName");

            EncryptedXml exml = new EncryptedXml(Doc);

            exml.AddKeyNameMapping(KeyName, Alg);
            exml.DecryptDocument();
        }

    }
}
