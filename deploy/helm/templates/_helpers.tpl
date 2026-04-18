{{/*
Common labels and helpers.
*/}}
{{- define "adaptiveapi.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{- define "adaptiveapi.fullname" -}}
{{- printf "%s-%s" .Release.Name (include "adaptiveapi.name" .) | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{- define "adaptiveapi.labels" -}}
app.kubernetes.io/name: {{ include "adaptiveapi.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
helm.sh/chart: {{ printf "%s-%s" .Chart.Name .Chart.Version }}
{{- end -}}

{{- define "adaptiveapi.selectorLabels" -}}
app.kubernetes.io/name: {{ include "adaptiveapi.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end -}}
