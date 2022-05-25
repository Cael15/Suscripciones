﻿using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using WebAPIAutores.DTOs;
using WebAPIAutores.Entidades;

namespace WebAPIAutores.Middlewares
{
    public static class LimitarPeticionesMiddlewareExtensions
    {
        public static IApplicationBuilder UseLimitarPeticiones(this IApplicationBuilder app)
        {
            return app.UseMiddleware<LimitarPeticionesMiddleware>();
        }
    }

    public class LimitarPeticionesMiddleware
    {
        private readonly RequestDelegate siguiente;
        private readonly IConfiguration configuration;

        public LimitarPeticionesMiddleware(RequestDelegate siguiente, IConfiguration configuration)
        {
            this.siguiente = siguiente;
            this.configuration = configuration;
        }

        public async Task InvokeAsync(HttpContext httpContext, ApplicationDbContext context)
        {
            var limitarPeticionesConfiguracion = new LimitarPeticionesConfiguracion();
            configuration.GetRequiredSection("limitarPeticiones").Bind(limitarPeticionesConfiguracion);

            var ruta = httpContext.Request.Path.ToString();
            var estaLaRutaEnListaBlanca = limitarPeticionesConfiguracion.ListaBlancaRutas.Any(x => ruta.Contains(x));

            if (estaLaRutaEnListaBlanca)
            {
                await siguiente(httpContext);
                return;
            }

            var llavesStringValues = httpContext.Request.Headers["X-Api-Key"];

            if (llavesStringValues.Count == 0)
            {
                httpContext.Response.StatusCode = 400;
                await httpContext.Response.WriteAsync("Debe proveer la llave en la cabecera X-Api-Key");
                return;
            }

            if (llavesStringValues.Count > 1)
            {
                httpContext.Response.StatusCode = 400;
                await httpContext.Response.WriteAsync("Solo una llave debe estar presente");
                return;
            }

            var llave = llavesStringValues[0];

            var llaveDB = await context.LlavesAPI.FirstOrDefaultAsync(x => x.Llave == llave);

            if (llaveDB == null)
            {
                httpContext.Response.StatusCode = 400;
                await httpContext.Response.WriteAsync("La lleve no exite");
                return;
            }

            if (!llaveDB.Activa)
            {
                httpContext.Response.StatusCode = 400;
                await httpContext.Response.WriteAsync("La lleve se encuentra inactiva");
                return;
            }

            if (llaveDB.TipoLlave == TipoLlave.Gratuita)
            {

                var hoy = DateTime.Today;
                var mañana = hoy.AddDays(1);
                var cantidadPeticionesRealizadasHoy = await context.Peticiones.CountAsync(x =>
                x.LlaveId == llaveDB.Id && x.FechaPeticion >= hoy && x.FechaPeticion < mañana);

                if (cantidadPeticionesRealizadasHoy >= limitarPeticionesConfiguracion.PeticionesPorDiaGratuito)
                {
                    httpContext.Response.StatusCode = 429; // Too many requests
                    await httpContext.Response.WriteAsync("Ha excedido el límite de peticiones por día. Si desea " +
                        "realizar más peticiones, " +
                        "actualice su suscripción a una cuenta profesional");
                    return;
                }
            }

            var superaRestricciones = PeticionSuperarAlgunaDeLasRestricciones(llaveDB, httpContext);

            if (!superaRestricciones)
            {
                httpContext.Response.StatusCode = 403;
                return;
            }

            var peticion = new Peticion() { LlaveId = llaveDB.Id, FechaPeticion = DateTime.UtcNow };
            context.Add(peticion);
            await context.SaveChangesAsync();

            await siguiente(httpContext);
;       }

        private bool PeticionSuperarAlgunaDeLasRestricciones(LlaveAPI llaveAPI, HttpContext httpContext)
        {
            var hayRestricciiones = llaveAPI.RestriccionesDominio.Any() || llaveAPI.RestriccionesIP.Any();

            if (!hayRestricciiones)
            {
                return true;
            }

            var peticionSuperaLasRestriccionesDeDominio =
                    PeticionSuperaLasRestriccionesDeDominio(llaveAPI.RestriccionesDominio, httpContext);

            return peticionSuperaLasRestriccionesDeDominio;

        }

        private bool PeticionSuperaLasRestriccionesDeDominio(List<RestriccionDominio> restricciones,
            HttpContext httpContext)
        {
            if ( restricciones == null || restricciones.Count == 0)
            {
                return false;
            }

            var referer = httpContext.Request.Headers["Referer"].ToString();

            if (referer == String.Empty)
            {
                return false;
            }

            Uri myUri = new Uri(referer);
            string host = myUri.Host;

            var superarRestriccion = restricciones.Any(x => x.Dominio == host);
            return superarRestriccion;
        }

        
    }
}