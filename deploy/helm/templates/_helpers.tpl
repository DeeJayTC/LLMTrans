{{/*
Common labels and helpers.
*/}}
{{- define "llmtrans.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{- define "llmtrans.fullname" -}}
{{- printf "%s-%s" .Release.Name (include "llmtrans.name" .) | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{- define "llmtrans.labels" -}}
app.kubernetes.io/name: {{ include "llmtrans.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
helm.sh/chart: {{ printf "%s-%s" .Chart.Name .Chart.Version }}
{{- end -}}

{{- define "llmtrans.selectorLabels" -}}
app.kubernetes.io/name: {{ include "llmtrans.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end -}}
