<template>
  <a-select
    :value="value || undefined"
    :options="options"
    show-search
    allow-clear
    :filter-option="filterOption"
    :placeholder="placeholder || '搜索图标名称'"
    :style="{ width: width || '100%' }"
    @update:value="(v: any) => emit('update:value', v ?? '')"
  >
    <template #option="{ label, icon }">
      <span class="icon-picker-option">
        <component :is="icon" class="icon-picker-preview" />
        <span>{{ label }}</span>
      </span>
    </template>
    <!-- 选中态回显图标 -->
    <template v-if="currentIcon" #suffixIcon>
      <component :is="currentIcon" />
    </template>
  </a-select>
</template>

<script setup lang="ts">
import { computed, ref } from 'vue';
import { menuIconOptions, resolveMenuIcon, type MenuIconOption } from '@/utils/menuIcons';

/**
 * 菜单图标选择器（老板令 2026-11-25 3g）：
 * 支持模糊输入的下拉，每项带图标预览 + 名称，可清空。
 * v-model:value 存语义名（如 Team），与侧边栏 renderMenuIcon 同口径。
 */
const props = defineProps<{
  value?: string;
  placeholder?: string;
  width?: string | number;
}>();

const emit = defineEmits<{
  (e: 'update:value', v: string): void;
}>();

const options = ref<MenuIconOption[]>(menuIconOptions());

const currentIcon = computed(() => resolveMenuIcon(props.value));

const filterOption = (input: string, option: MenuIconOption) =>
  option.value.toLowerCase().includes(input.trim().toLowerCase());
</script>

<style scoped>
.icon-picker-option {
  display: inline-flex;
  align-items: center;
  gap: 8px;
}

.icon-picker-preview {
  font-size: 16px;
}
</style>
