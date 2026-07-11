import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { EMPTY, Observable, catchError, forkJoin, map, of } from 'rxjs';
import { environment } from '../api/environment';
import { ClientContextService } from '../client/client-context.service';
import { CHART_HEALTH_MODULES, ChartReadinessReport, ModuleHealth } from './chart-health';

@Injectable({ providedIn: 'root' })
export class ChartHealthService {
  private readonly http = inject(HttpClient);
  private readonly client = inject(ClientContextService);

  /**
   * Reads chart readiness for the given modules (default all six). A failing host becomes an
   * errored entry, never a thrown stream. Callers pass the caller's visible subset so the widget
   * never requests a module the user lacks read capability for.
   */
  readiness(modules: { key: string; label: string }[] = CHART_HEALTH_MODULES): Observable<ModuleHealth[]> {
    const id = this.client.clientId();
    if (!id) return EMPTY;
    return forkJoin(
      modules.map(m =>
        this.http.get<ChartReadinessReport>(`${environment.apiBaseUrl}/clients/${id}/${m.key}/chart-readiness`).pipe(
          map((report): ModuleHealth => ({ key: m.key, label: m.label, report, errored: false })),
          catchError(() => of<ModuleHealth>({ key: m.key, label: m.label, report: null, errored: true })),
        ),
      ),
    );
  }
}
