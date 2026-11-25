<template>
  <div ref="rootRef" class="page-layout">
    <div class="page-layout-top">
      <slot></slot>
    </div>
    <div class="page-layout-body">
      <slot name="table" :scroll-y="scrollY"></slot>
    </div>
  </div>
</template>

<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue';

/**
 * 全站统一固定布局（规范 v2 §一4，评审定案）：
 * 整页不滚——顶部查询条件区 + 操作按钮区固定，中间表体滚动（scroll.y 动态高度），
 * 分页钉底右下角。列表页逐页迁移到本组件。
 * 用法：
 *   <PageLayout>
 *     <FilterBar ... />
 *     <div class="table-operator">...</div>
 *     <template #table="{ scrollY }">
 *       <a-table :scroll="{ x: N, y: scrollY }" ... />
 *     </template>
 *   </PageLayout>
 */
const rootRef = ref<HTMLElement>();
const scrollY = ref(320);

let ro: ResizeObserver | undefined;

/** 表体可用高度 = 布局体高度 - 表头(~55px) - 分页区(~56px) - 余量 */
function measure() {
  const body = rootRef.value?.querySelector('.page-layout-body') as HTMLElement | null;
  if (!body) return;
  scrollY.value = Math.max(200, body.clientHeight - 120);
}

onMounted(() => {
  ro = new ResizeObserver(measure);
  if (rootRef.value) ro.observe(rootRef.value);
  measure();
});

onBeforeUnmount(() => ro?.disconnect());
</script>

<style scoped>
.page-layout {
  height: 100%;
  display: flex;
  flex-direction: column;
  overflow: hidden;
}

.page-layout-top {
  flex-shrink: 0;
}

.page-layout-body {
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
}

/* a-table 内置分页钉底右下角：撑满高度，表体滚动，分页固定在下 */
.page-layout-body :deep(.ant-table-wrapper) {
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
}

.page-layout-body :deep(.ant-table-wrapper .ant-spin-nested-loading) {
  flex: 1;
  min-height: 0;
}

.page-layout-body :deep(.ant-table-wrapper .ant-spin-container) {
  height: 100%;
  display: flex;
  flex-direction: column;
}

.page-layout-body :deep(.ant-table-wrapper .ant-table) {
  flex: 1;
  min-height: 0;
}

.page-layout-body :deep(.ant-table-wrapper .ant-pagination) {
  flex-shrink: 0;
  margin: 12px 0 0;
  align-self: flex-end;
}
</style>
