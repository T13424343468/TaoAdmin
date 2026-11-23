<template>
  <div class="page-container">
    <a-page-header title="资源监控" sub-title="各服务 CPU / 内存 / 磁盘实时占用与告警" style="padding-left: 0; padding-top: 0">
      <template #extra>
        <a-button v-permission="'monitor:resource:manage'" @click="openThresholdModal">阈值设置</a-button>
        <a-button :loading="loading" @click="loadData">刷新</a-button>
      </template>
    </a-page-header>

    <a-spin :spinning="loading">
      <a-empty v-if="!loading && snapshots.length === 0" description="暂无资源采样数据" />

      <a-row :gutter="[16, 16]">
        <a-col v-for="snap in snapshots" :key="snap.serviceName" :xs="24" :sm="12" :xl="8">
          <a-card size="small" :class="['resource-card', { alerting: isAlerting(snap) }]" hoverable>
            <template #title>
              <a-badge
                :status="!snap.available ? 'error' : isAlerting(snap) ? 'error' : 'success'"
                :text="snap.serviceName"
                class="svc-title"
              />
            </template>
            <template #extra>
              <a-tag v-if="!snap.available" color="red">不可用</a-tag>
              <a-tag v-else-if="isAlerting(snap)" color="red">告警</a-tag>
              <a-tag v-else color="green">正常</a-tag>
            </template>

            <div class="metric">
              <div class="metric-label">CPU</div>
              <a-progress
                :percent="snap.cpuPercent"
                :format="fmtPercent"
                :status="metricStatus(snap.cpuPercent, thresholds?.cpuPercent)"
                :stroke-color="metricColor(snap.cpuPercent, thresholds?.cpuPercent)"
                size="small"
              />
            </div>
            <div class="metric">
              <div class="metric-label">内存</div>
              <a-progress
                :percent="snap.memoryPercent"
                :format="fmtPercent"
                :status="metricStatus(snap.memoryPercent, thresholds?.memoryPercent)"
                :stroke-color="metricColor(snap.memoryPercent, thresholds?.memoryPercent)"
                size="small"
              />
              <div class="metric-sub text-muted">{{ fmtMB(snap.memoryUsedMB) }} / {{ fmtMB(snap.memoryTotalMB) }}</div>
            </div>
            <div class="metric">
              <div class="metric-label">磁盘</div>
              <a-progress
                :percent="snap.diskPercent"
                :format="fmtPercent"
                :status="metricStatus(snap.diskPercent, thresholds?.diskPercent)"
                :stroke-color="metricColor(snap.diskPercent, thresholds?.diskPercent)"
                size="small"
              />
              <div class="metric-sub text-muted">{{ snap.diskUsedGB.toFixed(1) }} GB / {{ snap.diskTotalGB.toFixed(1) }} GB</div>
            </div>

            <a-descriptions :column="2" size="small" class="net-desc">
              <a-descriptions-item label="网络接收">{{ snap.netRxKBps.toFixed(1) }} KB/s</a-descriptions-item>
              <a-descriptions-item label="网络发送">{{ snap.netTxKBps.toFixed(1) }} KB/s</a-descriptions-item>
              <a-descriptions-item label="采样时间" :span="2">{{ snap.collectedAt || '-' }}</a-descriptions-item>
            </a-descriptions>
          </a-card>
        </a-col>
      </a-row>

      <!-- 近期资源告警事件 -->
      <a-card size="small" title="近期资源告警事件" class="alert-card">
        <a-table
          :data-source="alerts"
          :columns="alertColumns"
          :pagination="{ pageSize: 5, hideOnSinglePage: true }"
          row-key="eventId"
          size="small"
        >
          <template #emptyText>
            <a-empty description="暂无告警事件" />
          </template>
          <template #bodyCell="{ column, record }">
            <template v-if="column.key === 'severity'">
              <a-tag :color="severityColor(record.severity)">{{ record.severity }}</a-tag>
            </template>
            <template v-else-if="column.key === 'occurredAt'">
              {{ record.occurredAt }}
            </template>
          </template>
        </a-table>
      </a-card>
    </a-spin>

    <!-- 阈值设置弹窗 -->
    <a-modal v-model:open="thresholdVisible" title="资源异常阈值设置" :confirm-loading="saving" @ok="onSaveThresholds">
      <a-form :label-col="{ span: 8 }" style="margin-top: 12px">
        <a-form-item label="CPU 占用率（%）">
          <a-input-number v-model:value="thresholdForm.cpuPercent" :min="1" :max="100" style="width: 120px" />
        </a-form-item>
        <a-form-item label="内存占用率（%）">
          <a-input-number v-model:value="thresholdForm.memoryPercent" :min="1" :max="100" style="width: 120px" />
        </a-form-item>
        <a-form-item label="磁盘使用率（%）">
          <a-input-number v-model:value="thresholdForm.diskPercent" :min="1" :max="100" style="width: 120px" />
        </a-form-item>
      </a-form>
      <p class="text-muted">保存即生效，后台采样下一轮按新阈值评估并产生告警事件。</p>
    </a-modal>
  </div>
</template>

<script setup lang="ts">
import { ref, reactive, onMounted, onUnmounted } from 'vue';
import { message } from 'ant-design-vue';
import {
  getResourceCurrent,
  getResourceAlerts,
  getResourceThresholds,
  saveResourceThresholds,
  type ResourceSnapshotItem,
  type ResourceThresholds,
  type ResourceAlertEvent,
} from '@/api/monitor';

const loading = ref(false);
const snapshots = ref<ResourceSnapshotItem[]>([]);
const thresholds = ref<ResourceThresholds | null>(null);
const alerts = ref<ResourceAlertEvent[]>([]);
let timer: ReturnType<typeof setInterval> | null = null;

const thresholdVisible = ref(false);
const saving = ref(false);
const thresholdForm = reactive<ResourceThresholds>({ cpuPercent: 80, memoryPercent: 85, diskPercent: 90 });

const alertColumns = [
  { title: '级别', key: 'severity', dataIndex: 'severity', width: 90 },
  { title: '标题', key: 'title', dataIndex: 'title' },
  { title: '内容', key: 'content', dataIndex: 'content', ellipsis: true },
  { title: '来源', key: 'source', dataIndex: 'source', width: 180 },
  { title: '发生时间', key: 'occurredAt', dataIndex: 'occurredAt', width: 180 },
];

/** 是否有指标越过阈值（可用前提下） */
function isAlerting(snap: ResourceSnapshotItem): boolean {
  if (!snap.available || !thresholds.value) return false;
  return (
    snap.cpuPercent > thresholds.value.cpuPercent ||
    snap.memoryPercent > thresholds.value.memoryPercent ||
    snap.diskPercent > thresholds.value.diskPercent
  );
}

function metricStatus(value: number, threshold?: number): 'success' | 'exception' | 'normal' {
  if (threshold !== undefined && value > threshold) return 'exception';
  return 'normal';
}

function metricColor(value: number, threshold?: number): string {
  if (threshold !== undefined && value > threshold) return '#ff4d4f';
  if (threshold !== undefined && value > threshold * 0.8) return '#faad14';
  return '#52c41a';
}

function severityColor(severity: string): string {
  const map: Record<string, string> = { ERROR: 'red', WARN: 'orange', INFO: 'blue' };
  return map[severity?.toUpperCase()] || 'default';
}

function fmtMB(mb: number): string {
  return mb >= 1024 ? `${(mb / 1024).toFixed(2)} GB` : `${mb.toFixed(0)} MB`;
}

/** 进度条百分比文本：保留一位小数 */
function fmtPercent(value?: number): string {
  return `${(value ?? 0).toFixed(1)}%`;
}

async function loadData() {
  loading.value = true;
  try {
    const [current, alertResp] = await Promise.all([getResourceCurrent(), getResourceAlerts()]);
    snapshots.value = current?.items || [];
    thresholds.value = current?.thresholds || null;
    alerts.value = alertResp?.items || [];
  } finally {
    loading.value = false;
  }
}

async function openThresholdModal() {
  try {
    const t = await getResourceThresholds();
    if (t) {
      thresholdForm.cpuPercent = t.cpuPercent;
      thresholdForm.memoryPercent = t.memoryPercent;
      thresholdForm.diskPercent = t.diskPercent;
    }
  } finally {
    thresholdVisible.value = true;
  }
}

async function onSaveThresholds() {
  saving.value = true;
  try {
    await saveResourceThresholds({ ...thresholdForm });
    message.success('阈值已保存，立即生效');
    thresholdVisible.value = false;
    await loadData();
  } finally {
    saving.value = false;
  }
}

onMounted(() => {
  loadData();
  // 每 30 秒自动刷新
  timer = setInterval(loadData, 30000);
});

onUnmounted(() => {
  if (timer) clearInterval(timer);
});
</script>

<style scoped lang="less">
.resource-card {
  transition: box-shadow 0.2s;

  &.alerting {
    border-top: 2px solid #ff4d4f;
  }
}

.svc-title {
  font-weight: 600;
}

.metric {
  .metric-label {
    font-size: 12px;
    color: rgba(0, 0, 0, 0.45);
  }

  .metric-sub {
    font-size: 11px;
    margin-top: -6px;
  }
}

.net-desc {
  margin-top: 8px;
  border-top: 1px dashed #f0f0f0;
  padding-top: 8px;
}

.alert-card {
  margin-top: 16px;
}
</style>
