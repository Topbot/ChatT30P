﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Web;
using System.Xml;
using System.Xml.Serialization;
using TopTools.Azure;

namespace ChatT30P.Core
{
    public class MemberEntity : TableEntity
    {
        private static string ContainerName = "ChatUsers";
        private static XmlSerializer SerializerObj = new XmlSerializer(typeof(MemberEntity));
        /// <summary>
        /// Где храним
        /// </summary>
        private static string DataCacheDirectory
        {
            get
            {
                return Path.Combine(Environment.GetEnvironmentVariable("TEMP"), ContainerName);
            }
        }

        /// <summary>
        /// Где храним точки для карты
        /// </summary>
        private string FileName
        {
            get
            {
                return Path.Combine(DataCacheDirectory, this.RowKey.Replace("|", ""));//| - запрещен
            }
        }

        public MemberEntity()
        {
        }

        public MemberEntity(string username) : base("chat.t30p.ru", username)
        {
        }

        public MemberEntity(string hostname, string username)
            : base(hostname, username)
        {
        }

        /// <summary>
        /// Пароль
        /// </summary>
        public string Password { get; set; }

        /// <summary>
        /// Мыло
        /// </summary>
        public string Email { get; set; }

        /// <summary>
        /// Время оплаты сервиса
        /// </summary>
        public string IsPaid { get; set; }

        /// <summary>
        /// Админские права
        /// </summary>
        public string IsAdmin { get; set; }

        /// <summary>
        /// Внешний адресс
        /// </summary>
        public string Ip { get; set; }


        #region Public Function

        /// <summary>
        /// Загрузка из БД
        /// </summary>
        /// <param name="blogId"></param>
        /// <returns></returns>
        public static MemberEntity Load(string blogId, bool withAbout = false)
        {
            try
            {
                var temp = new MemberEntity(blogId);
                if (File.Exists(temp.FileName))
                {
                    //пробуем уже загрузиться из файла
                    using (var filedata = new FileStream(temp.FileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        using (var stream = new StreamReader(filedata, Encoding.UTF8))
                        {
                            return SerializerObj.Deserialize(new XmlTextReader(stream)) as MemberEntity;
                        }
                    }
                }
                //else
                //{
                //    var tbl = TopTools.Azure.BlobStorage.Table("socialt30pru");
                //    var item = (from i in tbl.CreateQuery<MemberEntity>()
                //                where i.PartitionKey == temp.PartitionKey && i.RowKey == temp.RowKey
                //                select i).FirstOrDefault();
                //    if (item != null)
                //    {
                //        //загрузили из таблиц, то запишем в файл
                //        item.Save(false);
                //    }
                //    return item;
                //}
            }
            catch (Exception e)
            {
                Trace.Write("blogId=" + blogId + Environment.NewLine + e);
            }
            return null;
        }

        /// <summary>
        /// Сохранение рейтинга в файл
        /// </summary>
        public void Save(bool renew = true)
        {
            try
            {
                if (!renew && File.Exists(FileName))
                {
                    return;//не перезаписываем
                }
                var sb = new StringBuilder();
                SerializerObj.Serialize(new StringWriter(sb), this);
                File.WriteAllText(FileName, sb.ToString());
            }
            catch (DirectoryNotFoundException noDir)
            {
                Directory.CreateDirectory(DataCacheDirectory);
            }
            catch (Exception e1)
            {
                Trace.Write(e1);
            }
            return;
            //var tbl = TopTools.Azure.BlobStorage.Table("rating");
            //try
            //{
            //    tbl.Execute(TableOperation.InsertOrMerge(this));
            //}
            //catch (StorageException e)
            //{
            //    var alltext = e.ToString();
            //    if (alltext.Contains("The property value exceeds the maximum allowed size"))
            //    {
            //        try
            //        {
            //            this.About = String.Empty;//зануляем описание
            //            tbl.Execute(TableOperation.InsertOrMerge(this));
            //        }
            //        catch (StorageException e2)
            //        {
            //            System.Diagnostics.Trace.Write(e2);
            //        }
            //    }
            //    else
            //    {
            //        System.Diagnostics.Trace.Write(e);
            //    }
            //}
        }

        public void Delete()
        {
            try
            {
                if (File.Exists(FileName))
                {
                    File.Delete(FileName);
                }
            }
            catch (Exception e)
            {
                System.Diagnostics.Trace.Write(e);
            }
        }

        /// <summary>
        /// Получение списка всех пользователей
        /// </summary>
        /// <returns></returns>
        public static List<MemberEntity> GetAllUsers()
        {
            List<MemberEntity> oRet = new List<MemberEntity>();
            try
            {
                //var tbl = TopTools.Azure.BlobStorage.Table("socialt30pru");
                //IQueryable<MemberEntity> q = tbl.CreateQuery<MemberEntity>();
                //oRet = (from i in q
                //            where i.PartitionKey == HttpContext.Current.Request.Url.Host
                //            select i).ToList();
                foreach (var file in Directory.GetFiles(DataCacheDirectory))
                {
                    oRet.Add(MemberEntity.Load(Path.GetFileName(file)));
                }
            }
            catch (DirectoryNotFoundException noDir)
            {
                Directory.CreateDirectory(DataCacheDirectory);
            }
            catch (Exception e1)
            {
                Trace.Write(e1);
            }
            return oRet;
        }

        #endregion

    }
}
